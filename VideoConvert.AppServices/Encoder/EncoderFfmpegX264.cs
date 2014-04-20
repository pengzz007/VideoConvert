﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="EncoderFfmpegX264.cs" company="JT-Soft (https://github.com/UniqProject/VideoConvert)">
//   This file is part of the VideoConvert.AppServices source code - It may be used under the terms of the GNU General Public License.
// </copyright>
// <summary>
//   The ffmpeg x264 encoder class
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace VideoConvert.AppServices.Encoder
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.IO.Pipes;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using DarLib;
    using log4net;
    using VideoConvert.AppServices.Decoder;
    using VideoConvert.AppServices.Encoder.Interfaces;
    using VideoConvert.AppServices.Services.Base;
    using VideoConvert.AppServices.Services.Interfaces;
    using VideoConvert.AppServices.Utilities;
    using VideoConvert.Interop.EventArgs;
    using VideoConvert.Interop.Model;
    using VideoConvert.Interop.Model.Profiles;
    using VideoConvert.Interop.Model.x264;
    using VideoConvert.Interop.Utilities;

    /// <summary>
    /// The ffmpeg x264 encoder class
    /// </summary>
    public class EncoderFfmpegX264 : EncodeBase, IEncoderFfmpegX264
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(EncoderFfmpegX264));

        #region Private Variables

        private static readonly string[] CliLevelNames =
        {
            "1", "11", "12", "13", "2", "21", "22", "3", "31",
            "32", "4", "41", "42", "5", "51", "52"
        };

        private const string Executable = "ffmpeg.exe";
        private const string Executable64 = "ffmpeg_64.exe";

        private readonly Regex _frameInformation =
            new Regex(@"^.*frame=\s*(\d*)\s*fps=\s*([\d\.]*).*time=\s*([\d\.\:]*).*bitrate=\s*([\d\.]*).*kbits/s.*$",
                RegexOptions.Singleline | RegexOptions.Multiline);

        /// <summary>
        /// The User Setting Service
        /// </summary>
        private readonly IAppConfigService _appConfig;

        /// <summary>
        /// Gets the Encoder Process ID
        /// </summary>
        private int _encoderProcessId;

        /// <summary>
        /// Gets the Decoder Process ID
        /// </summary>
        private int _decoderProcessId;

        /// <summary>
        /// Start time of the current Encode;
        /// </summary>
        private DateTime _startTime;

        /// <summary>
        /// The Current Task
        /// </summary>
        private EncodeInfo _currentTask;

        /// <summary>
        /// Gets the encoding pass
        /// </summary>
        private int _encodePass;

        private NamedPipeServerStream _decodePipe;
        private NamedPipeServerStream _encodePipe;

        private IAsyncResult _decodePipeState;
        private IAsyncResult _encodePipeState;

        private Thread _pipeReadThread;

        private X264Profile _encProfile;

        private string _outFile;

        private int _encodeMode;

        private long _frameCount;

        private TimeSpan _remainingTime;

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoderFfmpegX264"/> class.
        /// </summary>
        /// <param name="appConfig">
        /// The user Setting Service.
        /// </param>
        public EncoderFfmpegX264(IAppConfigService appConfig) : base(appConfig)
        {
            this._appConfig = appConfig;
            Log.Info("Encoder created");
        }

        #region Properties

        /// <summary>
        /// Gets or sets The x264 Process
        /// </summary>
        protected Process EncodeProcess { get; set; }

        /// <summary>
        /// Gets or sets the ffmpeg decode Process
        /// </summary>
        protected Process DecodeProcess { get; set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Execute the x264 process and read version info
        /// </summary>
        /// <param name="encPath">
        /// Stores the location of the executable
        /// </param>
        /// <param name="use64Bit">
        /// Defines whether 64bit version should be used
        /// </param>
        /// <returns>
        /// The version of the executable.
        /// </returns>
        public static string GetVersionInfo(string encPath, bool use64Bit)
        {
            var verInfo = string.Empty;

            if (use64Bit && !Environment.Is64BitOperatingSystem) return string.Empty;

            var localExecutable = Path.Combine(encPath, use64Bit ? Executable64 : Executable);

            using (var encoder = new Process())
            {
                var parameter = new ProcessStartInfo(localExecutable)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                encoder.StartInfo = parameter;

                bool started;
                try
                {
                    started = encoder.Start();
                }
                catch (Exception ex)
                {
                    started = false;
                    Log.ErrorFormat("ffmpeg exception: {0}", ex);
                }

                if (started)
                {
                    var output = encoder.StandardOutput.ReadToEnd();
                    var regObj = new Regex(@"^.*ffmpeg version ([\w\d\.\-_]+)[, ].*$",
                        RegexOptions.Singleline | RegexOptions.Multiline);
                    var result = regObj.Match(output);
                    if (result.Success)
                        verInfo = result.Groups[1].Value;

                    encoder.WaitForExit(10000);
                    if (!encoder.HasExited)
                        encoder.Kill();
                }
            }

            // Debug info
            if (Log.IsDebugEnabled)
            {
                if (use64Bit)
                    Log.Debug("Selected 64 bit encoder");
                Log.DebugFormat("ffmpeg \"{0}\" found", verInfo);
            }
            return verInfo;
        }

        /// <summary>
        /// Execute a ffmpeg process.
        /// This should only be called from the UI thread.
        /// </summary>
        /// <param name="encodeQueueTask">
        /// The encodeQueueTask.
        /// </param>
        public override void Start(EncodeInfo encodeQueueTask)
        {
            try
            {
                if (this.IsEncoding)
                {
                    encodeQueueTask.ExitCode = -1;
                    throw new Exception("ffmpeg is already encoding.");
                }

                this.IsEncoding = true;
                this._currentTask = encodeQueueTask;

                var use64BitEncoder = this._appConfig.Use64BitEncoders &&
                                      this._appConfig.X26464Installed &&
                                      Environment.Is64BitOperatingSystem;

                // TODO: this one is very ugly

                this._encProfile = (X264Profile)this._currentTask.VideoProfile;

                if (!this._currentTask.EncodingProfile.Deinterlace && this._currentTask.VideoStream.Interlaced)
                    this._currentTask.VideoStream.Interlaced = false;

                var resizeTo = VideoHelper.GetTargetSize(this._currentTask);

                if (string.IsNullOrEmpty(this._currentTask.AviSynthScript))
                {
                    var avsHelper = new AviSynthHelper(this._appConfig);
                    avsHelper.GenerateAviSynthScript(this._currentTask, resizeTo);
                }

                var inputFile = this._currentTask.AviSynthScript;

                this._outFile = FileSystemHelper.CreateTempFile(this._appConfig.DemuxLocation,
                                                                string.IsNullOrEmpty(this._currentTask.TempOutput) 
                                                                    ? this._currentTask.BaseName 
                                                                    : this._currentTask.TempOutput,
                                                                "encoded.ts");

                var targetBitrate = 0;
                if (this._currentTask.EncodingProfile.TargetFileSize > 0)
                    targetBitrate = VideoHelper.CalculateVideoBitrate(this._currentTask);

                this._encodeMode = this._encProfile.EncodingMode;
                this._frameCount = this._currentTask.VideoStream.FrameCount;
                this._encodePass = this._currentTask.StreamId;

                var ffmpegCliPath = Path.Combine(this._appConfig.ToolsPath,
                                                 use64BitEncoder ? Executable64 : Executable);

                var query = this.GenerateCommandLine(targetBitrate,
                                                     resizeTo.Width,
                                                     resizeTo.Height,
                                                     this._encodePass,
                                                     this._currentTask.VideoStream.FrameRateEnumerator,
                                                     this._currentTask.VideoStream.FrameRateDenominator,
                                                     this._currentTask.EncodingProfile.StereoType,
                                                     this._currentTask.VideoStream.PicSize,
                                                     this._appConfig.EncodeNamedPipeFullName, 
                                                     this._outFile);

                var cliStart = new ProcessStartInfo(ffmpegCliPath, query)
                                                    {
                                                        WorkingDirectory = this._appConfig.DemuxLocation,
                                                        RedirectStandardOutput = true,
                                                        RedirectStandardError = true,
                                                        UseShellExecute = false,
                                                        CreateNoWindow = true
                                                    };

                this.EncodeProcess = new Process { StartInfo = cliStart };
                Log.InfoFormat("start parameter: ffmpeg {0}", query);

                this._decodePipe = new NamedPipeServerStream(this._appConfig.DecodeNamedPipeName,
                                                             PipeDirection.InOut, 
                                                             3,
                                                             PipeTransmissionMode.Byte,
                                                             PipeOptions.Asynchronous);
                this._decodePipeState = this._decodePipe.BeginWaitForConnection(this.DecoderConnected, null);

                this._encodePipe = new NamedPipeServerStream(this._appConfig.EncodeNamedPipeName,
                                                             PipeDirection.InOut,
                                                             3,
                                                             PipeTransmissionMode.Byte,
                                                             PipeOptions.Asynchronous);
                this._encodePipeState = this._encodePipe.BeginWaitForConnection(this.EncoderConnected, null);

                var originalSize = new Size(this._currentTask.VideoStream.Width, this._currentTask.VideoStream.Height);
                if (this._currentTask.VideoStream.Width <
                    this._currentTask.VideoStream.Height * this._currentTask.VideoStream.AspectRatio)
                {
                    originalSize.Width =
                        (int) (this._currentTask.VideoStream.Height * this._currentTask.VideoStream.AspectRatio);
                    int temp;
                    Math.DivRem(originalSize.Width, 2, out temp);
                    originalSize.Width += temp;
                }

                this.DecodeProcess = DecoderFfmpeg.CreateDecodingProcess(inputFile,
                                                                         this._appConfig.Use64BitEncoders
                                                                         && this._appConfig.UseFfmpegScaling,
                                                                         originalSize,
                                                                         this._currentTask.VideoStream.AspectRatio,
                                                                         this._currentTask.VideoStream.CropRect, 
                                                                         resizeTo,
                                                                         this._appConfig.ToolsPath,
                                                                         this._appConfig.DecodeNamedPipeFullName);
                this.DecodeProcess.Start();
                this.EncodeProcess.Start();

                this._startTime = DateTime.Now;

                this.EncodeProcess.ErrorDataReceived += EncoderErrorDataReceived;
                this.EncodeProcess.BeginErrorReadLine();

                this.EncodeProcess.OutputDataReceived += EncoderOutputDataReceived;
                this.EncodeProcess.BeginOutputReadLine();

                this.DecodeProcess.BeginErrorReadLine();

                this._decoderProcessId = this.DecodeProcess.Id;
                this._encoderProcessId = this.EncodeProcess.Id;

                if (this._decoderProcessId != -1)
                {
                    this.DecodeProcess.EnableRaisingEvents = true;
                    this.DecodeProcess.Exited += DecodeProcessExited;
                }

                // Set the encoder process exit trigger
                if (this._encoderProcessId != -1)
                {
                    this.EncodeProcess.EnableRaisingEvents = true;
                    this.EncodeProcess.Exited += EncodeProcessExited;
                }

                this.DecodeProcess.PriorityClass = this._appConfig.GetProcessPriority();
                this.EncodeProcess.PriorityClass = this._appConfig.GetProcessPriority();

                // Fire the Encode Started Event
                this.InvokeEncodeStarted(EventArgs.Empty);
            }
            catch (Exception exc)
            {
                Log.Error(exc);
                this._currentTask.ExitCode = -1;
                this.IsEncoding = false;
                this.InvokeEncodeCompleted(new EncodeCompletedEventArgs(false, exc, exc.Message));
            }
        }


        /// <summary>
        /// Kill the CLI process
        /// </summary>
        public override void Stop()
        {
            try
            {
                if (this.EncodeProcess != null && !this.EncodeProcess.HasExited)
                {
                    this.EncodeProcess.Kill();
                }

                if (this.DecodeProcess != null && !this.DecodeProcess.HasExited)
                {
                    this.DecodeProcess.Kill();
                }
            }
            catch (Exception exc)
            {
                Log.Error(exc);
            }
            this.IsEncoding = false;
        }

        /// <summary>
        /// Shutdown the service.
        /// </summary>
        public void Shutdown()
        {
            // Nothing to do.
        }

        #endregion

        #region Private Helper Methods

        private void DecodeProcessExited(object sender, EventArgs e)
        {
            if (this._decodePipe != null)
            {
                try
                {
                    if (!this._decodePipeState.IsCompleted)
                        this._decodePipe.EndWaitForConnection(this._decodePipeState);
                }
                catch (Exception exc)
                {
                    Log.Error(exc);
                }
            }
        }

        /// <summary>
        /// The x264 process has exited.
        /// </summary>
        /// <param name="sender">
        /// The sender.
        /// </param>
        /// <param name="e">
        /// The EventArgs.
        /// </param>
        private void EncodeProcessExited(object sender, EventArgs e)
        {
            if (this._encodePipe != null)
            {
                try
                {
                    if (!this._encodePipeState.IsCompleted)
                        this._encodePipe.EndWaitForConnection(this._encodePipeState);
                }
                catch (Exception exc)
                {
                    Log.Error(exc);
                }
            }

            try
            {
                this.EncodeProcess.CancelErrorRead();
                this.EncodeProcess.CancelOutputRead();
            }
            catch (Exception exc)
            {
                Log.Error(exc);
            }

            this._currentTask.ExitCode = this.EncodeProcess.ExitCode;
            Log.InfoFormat("Exit Code: {0:g}", this._currentTask.ExitCode);

            if (this._currentTask.ExitCode == 0)
            {
                if ((this._encodeMode == 2 && this._encodePass == 2) ||
                    (this._encodeMode == 3 && this._encodePass == 3) ||
                    (this._encodeMode < 2 || this._encodePass > 3))
                {
                    this._currentTask.VideoStream.Encoded = true;
                    this._currentTask.VideoStream.IsRawStream = false;

                    this._currentTask.TempFiles.Add(this._currentTask.VideoStream.TempFile);
                    this._currentTask.VideoStream.TempFile = this._outFile;

                    try
                    {
                        this._currentTask.MediaInfo = GenHelper.GetMediaInfo(_outFile);
                    }
                    catch (Exception exc)
                    {
                        Log.Error(exc);
                    }

                    this._currentTask.VideoStream = VideoHelper.GetStreamInfo(this._currentTask.MediaInfo,
                        this._currentTask.VideoStream,
                        this._currentTask.EncodingProfile.OutFormat ==
                        OutputType.OutputBluRay);
                    this._currentTask.TempFiles.Add(Path.Combine(this._appConfig.DemuxLocation, "x264_2pass.log"));
                    this._currentTask.TempFiles.Add(Path.Combine(this._appConfig.DemuxLocation, "x264_2pass.log.mbtree"));
                    this._currentTask.TempFiles.Add(this._currentTask.AviSynthScript);
                    this._currentTask.TempFiles.Add(this._currentTask.FfIndexFile);
                    this._currentTask.TempFiles.Add(this._currentTask.AviSynthStereoConfig);
                }
            }

            this._currentTask.CompletedStep = this._currentTask.NextStep;
            this.IsEncoding = false;
            this.InvokeEncodeCompleted(new EncodeCompletedEventArgs(true, null, string.Empty));
        }

        /// <summary>
        /// Recieve the Standard Error information and process it
        /// </summary>
        /// <param name="sender">
        /// The Sender Object
        /// </param>
        /// <param name="e">
        /// DataReceived EventArgs
        /// </param>
        /// <remarks>
        /// Worker Thread.
        /// </remarks>
        private void EncoderErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data) && this.IsEncoding)
            {
                this.ProcessLogMessage(e.Data);
            }
        }

        /// <summary>
        /// The hb process output data received.
        /// </summary>
        /// <param name="sender">
        /// The sender.
        /// </param>
        /// <param name="e">
        /// The e.
        /// </param>
        private void EncoderOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!String.IsNullOrEmpty(e.Data) && this.IsEncoding)
            {
                this.ProcessLogMessage(e.Data);
            }
        }

        /// <summary>
        /// Process an Incomming Log Message.
        /// </summary>
        /// <param name="line">
        /// The log line.
        /// </param>
        protected void ProcessLogMessage(string line)
        {
            if (string.IsNullOrEmpty(line) || !this.IsEncoding) return;

            var frameMatch = _frameInformation.Match(line);
            // groups:
            // 1: actual frame
            // 2: fps
            // 3: position in stream (time)
            // 4: bitrate

            var eta = DateTime.Now.Subtract(_startTime);

            var codingFps = 0f;

            if (frameMatch.Success)
            {
                long current;
                Int64.TryParse(frameMatch.Groups[1].Value, NumberStyles.Number,
                    _appConfig.CInfo, out current);
                long framesRemaining = _frameCount - current;

                var percent = ((float)current / _frameCount) * 100;

                if (eta.Seconds != 0) // prevent division by zero
                {
                    //Frames per Second
                    codingFps = (float) Math.Round(current / eta.TotalSeconds, 2);
                }

                long secRemaining;
                if (codingFps > 1) // prevent another division by zero
                    secRemaining = framesRemaining / (int)codingFps;
                else
                    secRemaining = 0;

                if (secRemaining > 0)
                    _remainingTime = new TimeSpan(0, 0, (int)secRemaining);

                float fps;
                Single.TryParse(frameMatch.Groups[2].Value, NumberStyles.Number,
                    _appConfig.CInfo, out fps);
                float encBitrate;
                Single.TryParse(frameMatch.Groups[4].Value, NumberStyles.Number,
                    _appConfig.CInfo, out encBitrate);

                var eventArgs = new EncodeProgressEventArgs
                {
                    AverageFrameRate = codingFps,
                    CurrentFrameRate = fps,
                    CurrentFrame = current,
                    TotalFrames = this._frameCount,
                    EstimatedTimeLeft = _remainingTime,
                    PercentComplete = percent,
                    ElapsedTime = DateTime.Now - this._startTime,
                    Pass = this._encodePass,
                };

                this.InvokeEncodeStatusChanged(eventArgs);
            }
            else
            {
                Log.InfoFormat("ffmpeg: {0}", line);
            }
        }

        private void DecoderConnected(IAsyncResult ar)
        {
            Log.Info("Decoder Pipe connected");
            lock (this._decodePipe)
            {
                this._decodePipe.EndWaitForConnection(ar);
            }
        }

        private void EncoderConnected(IAsyncResult ar)
        {
            Log.Info("Encoder Pipe connected");
            lock (this._encodePipe)
            {
                this._encodePipe.EndWaitForConnection(ar);
            }

            this._pipeReadThread = new Thread(PipeReadThreadStart);
            this._pipeReadThread.Start();
            this._pipeReadThread.Priority = this._appConfig.GetThreadPriority();
        }

        private void PipeReadThreadStart()
        {
            try
            {
                if (this.EncodeProcess != null && this.DecodeProcess != null)
                    ReadThreadStart();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private void ReadThreadStart()
        {
            try
            {
                do
                {
                    Thread.Sleep(100);
                } while (!this._decodePipe.IsConnected || !this._encodePipe.IsConnected);

                var buffer = new byte[0xA00000]; // 10 MB

                int read = 0;

                do
                {
                    if (this._decodePipe.IsConnected)
                        read = this._decodePipe.Read(buffer, 0, buffer.Length);

                    if (this._encodePipe.IsConnected)
                        this._encodePipe.Write(buffer, 0, read);

                } while (read > 0 && this._decodePipe.IsConnected && this._encodePipe.IsConnected);

                this._encodePipe.Close();
                this._decodePipe.Close();
            }
            catch (Exception exc)
            {
                Log.Error(exc);
            }
        }

        private string GenerateCommandLine(int bitrate, int hRes, int vRes, int pass, int fpsN,
            int fpsD, StereoEncoding stereo = StereoEncoding.None, 
            VideoFormat format = VideoFormat.Unknown, string inFile = "input",
            string outFile = "output")
        {
            var sb = new StringBuilder();
            var x264Opts = new List<string>();
            var flags = new List<string>();

            if (!String.IsNullOrEmpty(inFile))
                sb.AppendFormat("-i \"{0}\" ", inFile);
            else
                sb.Append("-i - ");

            sb.AppendFormat("-map 0:v -vsync:v 1 -r:v {0:0}/{1:0} -c:v libx264 ", fpsN, fpsD);

            if (_encProfile != null)
            {
                bool display;
                var device = X264Device.CreateDeviceList()[_encProfile.TuneDevice];

                // AVC Profiles
                switch (_encProfile.AvcProfile)
                {
                    case 0:
                        sb.Append("-profile:v baseline ");
                        break;
                    case 1:
                        sb.Append("-profile:v main ");
                        break;
                    default:
                        sb.Append("-profile:v high ");
                        break;
                }

                // bitrate
                var tempBitrate = bitrate;
                var vbvBuf = GetVBVMaxrate(_encProfile, device);

                if (tempBitrate <= 0)
                    tempBitrate = _encProfile.VbrSetting;

                if (vbvBuf > 0 && tempBitrate > vbvBuf)   // limit Bitrate to max vbvbuf size
                    tempBitrate = vbvBuf;

                // AVC Levels
                if (_encProfile.AvcLevel != 16) // unrestricted
                {
                    int avcLevelBackup = _encProfile.AvcLevel;

                    int avcLevel = X264Settings.GetMinLevelForRes(hRes, vRes, fpsN, fpsD, bitrate, _encProfile.EncodingMode, _encProfile.AvcProfile);
                    if (avcLevel > _encProfile.AvcLevel)
                        _encProfile.AvcLevel = avcLevel;

                    sb.AppendFormat("-level {0} ", CliLevelNames[_encProfile.AvcLevel]);

                    _encProfile.AvcLevel = avcLevelBackup;
                }
                    
                // x264 Presets
                if (!_encProfile.CustomCommandLine.Contains("-preset"))
                {
                    switch (_encProfile.Preset)
                    {
                        case 0: sb.Append("-preset ultrafast "); break;
                        case 1: sb.Append("-preset superfast "); break;
                        case 2: sb.Append("-preset veryfast "); break;
                        case 3: sb.Append("-preset faster "); break;
                        case 4: sb.Append("-preset fast "); break;
                            //case 5: sb.Append("--preset medium "); break; // default value
                        case 6: sb.Append("-preset slow "); break;
                        case 7: sb.Append("-preset slower "); break;
                        case 8: sb.Append("-preset veryslow "); break;
                        case 9: sb.Append("-preset placebo "); break;
                    }
                }

                // x264 Tunings
                if (!_encProfile.CustomCommandLine.Contains("-tune"))
                {
                    switch (_encProfile.Tuning)
                    {
                        case 1: sb.Append("-tune film "); break;
                        case 2: sb.Append("-tune animation "); break;
                        case 3: sb.Append("-tune grain "); break;
                        case 4: sb.Append("-tune psnr "); break;
                        case 5: sb.Append("-tune ssim "); break;
                        case 6: sb.Append("-tune fastdecode "); break;
                    }
                }

                // Blu-Ray compatibility
                if (_encProfile.UseBluRayCompatibility)
                    sb.Append("-bluray-compat 1 ");

                // Encoding Modes
                var tempPass = pass;

                switch (_encProfile.EncodingMode)
                {
                    case 0: // ABR
                        if (!_encProfile.CustomCommandLine.Contains("-b:v"))
                            sb.AppendFormat(_appConfig.CInfo, "-b:v {0:0}k ", tempBitrate);
                        break;
                    case 1: // Constant Quantizer
                        if (!_encProfile.CustomCommandLine.Contains("-qp"))
                            sb.AppendFormat(_appConfig.CInfo, "-qp {0:0}", _encProfile.QuantizerSetting);
                        break;
                    case 2: // automated 2 pass
                    case 3: // automated 3 pass
                        sb.AppendFormat(_appConfig.CInfo, "-pass {0:0} -b:v {1:0}k ", tempPass, tempBitrate);
                        break;
                    default:
                        if (!_encProfile.CustomCommandLine.Contains("-crf") && _encProfile.QualitySetting != 23)
                            sb.AppendFormat(_appConfig.CInfo, "-crf {0:0} ", _encProfile.QualitySetting);
                        break;
                }

                // Slow 1st Pass
                if (!_encProfile.CustomCommandLine.Contains("-fastfirstpass"))
                    if (_encProfile.UseSlowFirstPass && _encProfile.Preset < 9 && // 9 = placebo
                        (_encProfile.EncodingMode == 2 || // automated twopass
                         (_encProfile.EncodingMode == 3)))  // automated threepass
                        sb.Append("-fastfirstpass 0 ");

                // Threads
                if (!_encProfile.CustomCommandLine.Contains("thread-input"))
                    if (_encProfile.UseThreadInput && _encProfile.NumThreads == 1)
                        x264Opts.Add("thread-input=1");
                if (!_encProfile.CustomCommandLine.Contains("-threads"))
                    if (_encProfile.NumThreads > 0)
                        sb.Insert(0, string.Format("-threads {0:0} ", _encProfile.NumThreads));

                #region frame-type tab

                // H.264 Features
                if (_encProfile.UseDeblocking)
                {
                    display = false;
                    switch (_encProfile.Tuning)
                    {
                        case 1: if (_encProfile.DeblockingStrength != -1 || _encProfile.DeblockingThreshold != -1) display = true; break; // film
                        case 2: if (_encProfile.DeblockingStrength != 1 || _encProfile.DeblockingThreshold != 1) display = true; break; // animation
                        case 3: if (_encProfile.DeblockingStrength != -2 || _encProfile.DeblockingThreshold != -2) display = true; break; // grain
                        default: if (_encProfile.DeblockingStrength != 0 || _encProfile.DeblockingThreshold != 0) display = true;
                            break;
                    }

                    if (!_encProfile.CustomCommandLine.Contains("-deblock "))
                        if (display)
                            sb.AppendFormat(_appConfig.CInfo, "-deblock {0:0}:{1:0} ", _encProfile.DeblockingStrength,
                                _encProfile.DeblockingThreshold);
                }
                else
                {
                    if (!_encProfile.CustomCommandLine.Contains("no-deblock="))
                        if (_encProfile.Preset != 0 && _encProfile.Tuning != 7) // ultrafast preset and not fast decode tuning
                            x264Opts.Add("no-deblock=1");
                }

                if (_encProfile.AvcProfile > 0 && !_encProfile.CustomCommandLine.Contains("no-cabac="))
                {
                    if (!_encProfile.UseCabac)
                    {
                        if (_encProfile.Preset != 0 && _encProfile.Tuning != 7) // ultrafast preset and not fast decode tuning
                            x264Opts.Add("no-cabac=1");
                    }
                }

                // GOP Size
                var backupMaxGopSize = _encProfile.MaxGopSize;
                var backupMinGopSize = _encProfile.MinGopSize;

                _encProfile.MaxGopSize = GetKeyInt(fpsN, fpsD, backupMaxGopSize, device, _encProfile.GopCalculation);

                if (_encProfile.MaxGopSize != 250) // default size
                {
                    x264Opts.Add(_encProfile.MaxGopSize == 0
                        ? "keyint=infinite"
                        : string.Format("keyint={0:0}", _encProfile.MaxGopSize));
                }

                if (!_encProfile.UseBluRayCompatibility)
                {
                    _encProfile.MinGopSize = GetMinKeyInt(fpsN, fpsD, backupMinGopSize, _encProfile.MaxGopSize, device,
                        _encProfile.GopCalculation);
                    if (_encProfile.MinGopSize > (_encProfile.MaxGopSize / 2 + 1))
                    {
                        _encProfile.MinGopSize = _encProfile.MaxGopSize / 2 + 1;
                    }
                    var Default = Math.Min(_encProfile.MaxGopSize / 10, fpsN / fpsD);

                    if (_encProfile.MinGopSize != Default) // (MIN(--keyint / 10,--fps)) is default
                        x264Opts.Add(string.Format("min-keyint={0:0}", _encProfile.MinGopSize));
                }

                _encProfile.MaxGopSize = backupMaxGopSize;
                _encProfile.MinGopSize = backupMinGopSize;

                if (!_encProfile.CustomCommandLine.Contains("open-gop)") &&
                    (_encProfile.UseOpenGop || _encProfile.UseBluRayCompatibility))
                    x264Opts.Add("open-gop=1");

                // B-Frames
                _encProfile.NumBFrames = GetBFrames(_encProfile, device);
                if (_encProfile.AvcProfile > 0 &&
                    _encProfile.NumBFrames != X264Settings.GetDefaultNumberOfBFrames(_encProfile.AvcLevel,
                                                                                     _encProfile.Tuning,
                                                                                     _encProfile.AvcProfile, 
                                                                                     device))
                {
                    x264Opts.Add(string.Format("bframes={0:0}", _encProfile.NumBFrames));
                }

                if (_encProfile.NumBFrames > 0)
                {
                    if (!_encProfile.CustomCommandLine.Contains("b-adapt="))
                    {
                        display = false;
                        if (_encProfile.Preset > 5) // medium
                        {
                            if (_encProfile.AdaptiveBFrames != 2)
                                display = true;
                        }
                        else if (_encProfile.Preset > 0) // ultrafast
                        {
                            if (_encProfile.AdaptiveBFrames != 1)
                                display = true;
                        }
                        else
                        {
                            if (_encProfile.AdaptiveBFrames != 0)
                                display = true;
                        }
                        if (display)
                            x264Opts.Add(string.Format("b-adapt={0:0}", _encProfile.AdaptiveBFrames));
                    }

                    _encProfile.BPyramid = GetBPyramid(_encProfile, device);
                    if (_encProfile.NumBFrames > 1 && (_encProfile.BPyramid != 2 && !_encProfile.UseBluRayCompatibility || _encProfile.BPyramid != 1 && _encProfile.UseBluRayCompatibility))
                    {
                        switch (_encProfile.BPyramid) // pyramid needs a minimum of 2 b frames
                        {
                            case 2:
                                sb.Append("-b-pyramid normal ");
                                break;
                            case 1: 
                                sb.Append("-b-pyramid strict ");
                                break;
                            case 0: 
                                sb.Append("-b-pyramid none ");
                                break;
                        }
                    }

                    if (!_encProfile.CustomCommandLine.Contains("-weightb "))
                        if (!_encProfile.UseWeightedPred && _encProfile.Tuning != 7 && _encProfile.Preset != 0) // no weightpredb + tuning != fastdecode + preset != ultrafast
                            sb.Append("-weightb 0");
                }

                // B-Frames bias
                if (!_encProfile.CustomCommandLine.Contains("-b-bias "))
                    if (_encProfile.BFrameBias != 0)
                        sb.AppendFormat(_appConfig.CInfo, "-b-bias {0:0} ", _encProfile.BFrameBias);


                // Other
                if (_encProfile.UseAdaptiveIFrameDecision)
                {
                    if (!_encProfile.CustomCommandLine.Contains("scenecut="))
                        if (_encProfile.NumExtraIFrames != 40 && _encProfile.Preset != 0 ||
                            _encProfile.NumExtraIFrames != 0 && _encProfile.Preset == 0)
                            x264Opts.Add(string.Format("scenecut={0:0}", _encProfile.NumExtraIFrames));
                }
                else
                {
                    if (!_encProfile.CustomCommandLine.Contains("no-scenecut="))
                        if (_encProfile.Preset != 0)
                            x264Opts.Add("no-scenecut=1");
                }


                // reference frames
                var iRefFrames = GetRefFrames(hRes, vRes, _encProfile, device);
                if (iRefFrames != X264Settings.GetDefaultNumberOfRefFrames(_encProfile.Preset, _encProfile.Tuning, null,
                                                                           _encProfile.AvcLevel, hRes, vRes))
                {
                    sb.AppendFormat(_appConfig.CInfo, "-refs {0:0} ", iRefFrames);
                    x264Opts.Add(string.Format("ref={0:0}", iRefFrames));
                }

                // WeightedPPrediction
                _encProfile.PFrameWeightedPrediction = GetWeightp(_encProfile, device);
                if (_encProfile.PFrameWeightedPrediction != X264Settings.GetDefaultNumberOfWeightp(_encProfile.Preset,
                                                                                                   _encProfile.Tuning,
                                                                                                   _encProfile.AvcProfile,
                                                                                                   _encProfile.UseBluRayCompatibility))
                    sb.AppendFormat(_appConfig.CInfo, "-weightp {0:0} ", _encProfile.PFrameWeightedPrediction);

                // Slicing
                _encProfile.NumSlices = GetSlices(_encProfile, device);
                if (_encProfile.NumSlices != 0)
                    x264Opts.Add(string.Format("slices={0:0}", _encProfile.NumSlices));

                if (!_encProfile.CustomCommandLine.Contains("-slice-max-size "))
                    if (_encProfile.MaxSliceSizeBytes != 0)
                        sb.AppendFormat(_appConfig.CInfo, "-slice-max-size {0:0} ", _encProfile.MaxSliceSizeBytes);

                if (!_encProfile.CustomCommandLine.Contains("slice-max-mbs="))
                    if (_encProfile.MaxSliceSizeBlocks != 0)
                        x264Opts.Add(string.Format("slice-max-mbs={0:0}", _encProfile.MaxSliceSizeBlocks));

                #endregion

                #region rc tab

                if (!_encProfile.CustomCommandLine.Contains("qpmin="))
                    if (_encProfile.QuantizerMin != 0)
                        x264Opts.Add(string.Format("qpmin={0:0}", _encProfile.QuantizerMin));

                if (!_encProfile.CustomCommandLine.Contains("qpmax="))
                    if (_encProfile.QuantizerMax != 69)
                        x264Opts.Add(string.Format("qpmax={0:0}", _encProfile.QuantizerMax));

                if (!_encProfile.CustomCommandLine.Contains("qpstep="))
                    if (_encProfile.QuantizerDelta != 4)
                        x264Opts.Add(string.Format("qpstep={0:0}", _encProfile.QuantizerDelta));

                if (Math.Abs(_encProfile.QuantizerRatioIp - 1.4F) > 0)
                {
                    display = true;
                    if (_encProfile.Tuning == 3 && Math.Abs(_encProfile.QuantizerRatioIp - 1.1F) <= 0)
                        display = false;

                    if (!_encProfile.CustomCommandLine.Contains("ipratio="))
                        if (display)
                            x264Opts.Add(string.Format("ipratio={0:0}", _encProfile.QuantizerRatioIp));
                }

                if (Math.Abs(_encProfile.QuantizerRatioPb - 1.3F) > 0)
                {
                    display = true;
                    if (_encProfile.Tuning == 3 && Math.Abs(_encProfile.QuantizerRatioPb - 1.1F) <= 0)
                        display = false;

                    if (!_encProfile.CustomCommandLine.Contains("pbratio="))
                        if (display)
                            x264Opts.Add(string.Format("pbratio={0:0}", _encProfile.QuantizerRatioPb));
                }

                if (!_encProfile.CustomCommandLine.Contains("chroma-qp-offset="))
                    if (_encProfile.ChromaQpOffset != 0)
                        x264Opts.Add(string.Format("chroma-qp-offset={0:0}", _encProfile.ChromaQpOffset));

                if (_encProfile.EncodingMode != 1) // doesn't apply to CQ mode
                {
                    _encProfile.VbvBufSize = GetVBVBufsize(_encProfile, device);
                    if (_encProfile.VbvBufSize > 0)
                        x264Opts.Add(string.Format("vbv-bufsize={0:0}", _encProfile.VbvBufSize));

                    _encProfile.VbvMaxRate = GetVBVMaxrate(_encProfile, device);
                    if (_encProfile.VbvMaxRate > 0)
                        x264Opts.Add(string.Format("vbv-maxrate={0:0}", _encProfile.VbvMaxRate));

                    if (!_encProfile.CustomCommandLine.Contains("vbv-init="))
                        if (Math.Abs(_encProfile.VbvInitialBuffer - 0.9F) > 0)
                            x264Opts.Add(string.Format(_appConfig.CInfo, "vbv-init={0:0.0}", _encProfile.VbvInitialBuffer));

                    if (!_encProfile.CustomCommandLine.Contains("ratetol="))
                        if (Math.Abs(_encProfile.BitrateVariance - 1.0F) > 0)
                            x264Opts.Add(string.Format(_appConfig.CInfo, "ratetol={0:0.0}", _encProfile.BitrateVariance));

                    if (!_encProfile.CustomCommandLine.Contains("qcomp="))
                    {
                        display = true;
                        if ((_encProfile.Tuning == 3 && Math.Abs(_encProfile.QuantizerCompression - 0.8F) <= 0) || (_encProfile.Tuning != 3 && Math.Abs(_encProfile.QuantizerCompression - 0.6F) <= 0))
                            display = false;
                        if (display)
                            x264Opts.Add(string.Format(_appConfig.CInfo, "qcomp={0:0.0}", _encProfile.QuantizerCompression));
                    }

                    if (_encProfile.EncodingMode > 1) // applies only to twopass
                    {
                        if (!_encProfile.CustomCommandLine.Contains("-cplxblur"))
                            if (_encProfile.TempBlurFrameComplexity != 20)
                                sb.AppendFormat(_appConfig.CInfo, "-cplxblur {0:0} ", _encProfile.TempBlurFrameComplexity);

                        if (!_encProfile.CustomCommandLine.Contains("qblur="))
                            if (Math.Abs(_encProfile.TempBlurQuant - 0.5F) > 0)
                                x264Opts.Add(string.Format(_appConfig.CInfo, "qblur={0:0.0}", _encProfile.TempBlurQuant));
                    }
                }

                // Dead Zones
                if (!_encProfile.CustomCommandLine.Contains("deadzone-inter="))
                {
                    display = true;
                    if ((_encProfile.Tuning != 3 && _encProfile.DeadZoneInter == 21 && _encProfile.DeadZoneIntra == 11) ||
                        (_encProfile.Tuning == 3 && _encProfile.DeadZoneInter == 6 && _encProfile.DeadZoneIntra == 6))
                        display = false;
                    if (display)
                        x264Opts.Add(string.Format("deadzone-inter={0:0}", _encProfile.DeadZoneInter));
                }

                if (!_encProfile.CustomCommandLine.Contains("deadzone-intra="))
                {
                    display = true;
                    if ((_encProfile.Tuning != 3 && _encProfile.DeadZoneIntra == 11) || (_encProfile.Tuning == 3 && _encProfile.DeadZoneIntra == 6))
                        display = false;
                    if (display)
                        x264Opts.Add(string.Format("deadzone-intra={0:0}", _encProfile.DeadZoneIntra));
                }

                // Disable Macroblok Tree
                if (!_encProfile.UseMbTree)
                {
                    if (!_encProfile.CustomCommandLine.Contains("-mbtree "))
                        if (_encProfile.Preset > 0) // preset veryfast
                            sb.Append("-mbtree 0 ");
                }
                else
                {
                    // RC Lookahead
                    if (!_encProfile.CustomCommandLine.Contains("-rc-lookahead "))
                    {
                        display = false;
                        switch (_encProfile.Preset)
                        {
                            case 0:
                            case 1: if (_encProfile.NumFramesLookahead != 0) display = true; break;
                            case 2: if (_encProfile.NumFramesLookahead != 10) display = true; break;
                            case 3: if (_encProfile.NumFramesLookahead != 20) display = true; break;
                            case 4: if (_encProfile.NumFramesLookahead != 30) display = true; break;
                            case 5: if (_encProfile.NumFramesLookahead != 40) display = true; break;
                            case 6: if (_encProfile.NumFramesLookahead != 50) display = true; break;
                            case 7:
                            case 8:
                            case 9: if (_encProfile.NumFramesLookahead != 60) display = true; break;
                        }
                        if (display)
                            sb.AppendFormat("-rc-lookahead {0:0} ", _encProfile.NumFramesLookahead);
                    }
                }

                // AQ-Mode
                if (_encProfile.EncodingMode != 1)
                {
                    if (!_encProfile.CustomCommandLine.Contains("-aq-mode "))
                    {
                        if (_encProfile.AdaptiveQuantizersMode != X264Settings.GetDefaultAqMode(_encProfile.Preset, _encProfile.Tuning))
                            sb.AppendFormat("-aq-mode {0:0} ", _encProfile.AdaptiveQuantizersMode);
                    }

                    if (_encProfile.AdaptiveQuantizersMode > 0)
                    {
                        display = false;
                        switch (_encProfile.Tuning)
                        {
                            case 2: if (Math.Abs(_encProfile.AdaptiveQuantizersStrength - 0.6F) > 0) display = true; break;
                            case 3: if (Math.Abs(_encProfile.AdaptiveQuantizersStrength - 0.5F) > 0) display = true; break;
                            case 7: if (Math.Abs(_encProfile.AdaptiveQuantizersStrength - 1.3F) > 0) display = true; break;
                            default: if (Math.Abs(_encProfile.AdaptiveQuantizersStrength - 1.0F) > 0) display = true; break;
                        }
                        if (!_encProfile.CustomCommandLine.Contains("-aq-strength "))
                            if (display)
                                sb.AppendFormat(_appConfig.CInfo, "-aq-strength {0:0.0} ", _encProfile.AdaptiveQuantizersStrength);
                    }
                }

                // custom matrices 
                if (_encProfile.AvcProfile > 1 && _encProfile.QuantizerMatrix > 0)
                {
                    switch (_encProfile.QuantizerMatrix)
                    {
                        case 1: 
                            if (!_encProfile.CustomCommandLine.Contains("cqm=")) 
                                x264Opts.Add("cqm=\"jvt\"");
                            break;
                    }
                }
                #endregion

                #region analysis tab

                // Disable Chroma Motion Estimation
                if (!_encProfile.CustomCommandLine.Contains("no-chroma-me"))
                    if (!_encProfile.UseChromaMotionEstimation)
                        x264Opts.Add("no-chroma-me=1");

                // Motion Estimation Range
                if (!_encProfile.CustomCommandLine.Contains("merange="))
                {
                    if ((_encProfile.Preset <= 7 && _encProfile.MotionEstimationRange != 16) ||
                        (_encProfile.Preset >= 8 && _encProfile.MotionEstimationRange != 24))
                        x264Opts.Add(string.Format("merange={0:0}", _encProfile.MotionEstimationRange));
                }

                // ME Type
                if (!_encProfile.CustomCommandLine.Contains("me="))
                {
                    display = false;
                    switch (_encProfile.Preset)
                    {
                        case 0:
                        case 1: if (_encProfile.MotionEstimationAlgorithm != 0) display = true; break;
                        case 2:
                        case 3:
                        case 4:
                        case 5: if (_encProfile.MotionEstimationAlgorithm != 1) display = true; break;
                        case 6:
                        case 7:
                        case 8: if (_encProfile.MotionEstimationAlgorithm != 2) display = true; break;
                        case 9: if (_encProfile.MotionEstimationAlgorithm != 4) display = true; break;
                    }

                    if (display)
                    {
                        switch (_encProfile.MotionEstimationAlgorithm)
                        {
                            case 0: x264Opts.Add("me=dia"); break;
                            case 1: x264Opts.Add("me=hex"); break;
                            case 2: x264Opts.Add("me=umh"); break;
                            case 3: x264Opts.Add("me=esa"); break;
                            case 4: x264Opts.Add("me=tesa"); break;
                        }
                    }

                }

                if (!_encProfile.CustomCommandLine.Contains("-direct-pred "))
                {
                    display = false;
                    if (_encProfile.Preset > 5) // preset medium
                    {
                        if (_encProfile.MvPredictionMod != 3)
                            display = true;
                    }
                    else if (_encProfile.MvPredictionMod != 1)
                        display = true;

                    if (display)
                    {
                        switch (_encProfile.MvPredictionMod)
                        {
                            case 0: sb.Append("-direct-pred none "); break;
                            case 1: sb.Append("-direct-pred spatial "); break;
                            case 2: sb.Append("-direct-pred temporal "); break;
                            case 3: sb.Append("-direct-pred auto "); break;
                        }
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("nr="))
                    if (_encProfile.NoiseReduction > 0)
                        x264Opts.Add(string.Format("nr={0:0}", _encProfile.NoiseReduction));


                // subpel refinement
                if (!_encProfile.CustomCommandLine.Contains("subme="))
                {
                    display = false;
                    switch (_encProfile.Preset)
                    {
                        case 0: if (_encProfile.SubPixelRefinement != 0) display = true; break;
                        case 1: if (_encProfile.SubPixelRefinement != 1) display = true; break;
                        case 2: if (_encProfile.SubPixelRefinement != 2) display = true; break;
                        case 3: if (_encProfile.SubPixelRefinement != 4) display = true; break;
                        case 4: if (_encProfile.SubPixelRefinement != 6) display = true; break;
                        case 5: if (_encProfile.SubPixelRefinement != 7) display = true; break;
                        case 6: if (_encProfile.SubPixelRefinement != 8) display = true; break;
                        case 7: if (_encProfile.SubPixelRefinement != 9) display = true; break;
                        case 8: if (_encProfile.SubPixelRefinement != 10) display = true; break;
                        case 9: if (_encProfile.SubPixelRefinement != 11) display = true; break;
                    }
                    if (display)
                        x264Opts.Add(string.Format("subme={0:0}", _encProfile.SubPixelRefinement));
                }

                // macroblock types
                if (!_encProfile.CustomCommandLine.Contains("-partitions "))
                {
                    var bExpectedP8X8Mv = true;
                    var bExpectedB8X8Mv = true;
                    var bExpectedI4X4Mv = true;
                    var bExpectedI8X8Mv = true;
                    var bExpectedP4X4Mv = true;

                    switch (_encProfile.Preset)
                    {
                        case 0:
                            bExpectedP8X8Mv = false;
                            bExpectedB8X8Mv = false;
                            bExpectedI4X4Mv = false;
                            bExpectedI8X8Mv = false;
                            bExpectedP4X4Mv = false;
                            break;
                        case 1:
                            bExpectedP8X8Mv = false;
                            bExpectedB8X8Mv = false;
                            bExpectedP4X4Mv = false;
                            break;
                        case 2:
                        case 3:
                        case 4:
                        case 5:
                        case 6:
                            bExpectedP4X4Mv = false;
                            break;
                    }
                    if (_encProfile.Tuning == 7 && bExpectedP8X8Mv)
                        bExpectedP4X4Mv = true;

                    if (_encProfile.AvcProfile < 2)
                        bExpectedI8X8Mv = false;

                    if (bExpectedP8X8Mv != _encProfile.MacroBlocksPartitionsP8X8 || bExpectedB8X8Mv != _encProfile.MacroBlocksPartitionsB8X8
                        || bExpectedI4X4Mv != _encProfile.MacroBlocksPartitionsI4X4 || bExpectedI8X8Mv != _encProfile.MacroBlocksPartitionsI8X8
                        || bExpectedP4X4Mv != _encProfile.MacroBlocksPartitionsP4X4)
                    {
                        if (_encProfile.MacroBlocksPartitionsP8X8 ||
                            _encProfile.MacroBlocksPartitionsB8X8 ||
                            _encProfile.MacroBlocksPartitionsI4X4 ||
                            _encProfile.MacroBlocksPartitionsI8X8 ||
                            _encProfile.MacroBlocksPartitionsP4X4)
                        {
                            sb.Append("-partitions ");
                            if (_encProfile.MacroBlocksPartitionsI4X4 &&
                                _encProfile.MacroBlocksPartitionsI8X8 &&
                                _encProfile.MacroBlocksPartitionsP4X4 &&
                                _encProfile.MacroBlocksPartitionsP8X8 &&
                                _encProfile.MacroBlocksPartitionsB8X8)
                                sb.Append("all ");
                            else
                            {
                                if (_encProfile.MacroBlocksPartitionsP8X8) // default is checked
                                    sb.Append("p8x8,");
                                if (_encProfile.MacroBlocksPartitionsB8X8) // default is checked
                                    sb.Append("b8x8,");
                                if (_encProfile.MacroBlocksPartitionsI4X4) // default is checked
                                    sb.Append("i4x4,");
                                if (_encProfile.MacroBlocksPartitionsP4X4) // default is unchecked
                                    sb.Append("p4x4,");
                                if (_encProfile.MacroBlocksPartitionsI8X8) // default is checked
                                    sb.Append("i8x8");
                                if (sb.ToString().EndsWith(","))
                                    sb.Remove(sb.Length - 1, 1);
                            }

                            if (!sb.ToString().EndsWith(" "))
                                sb.Append(" ");
                        }
                        else
                            sb.Append("-partitions none ");
                    }
                }

                if (_encProfile.AvcProfile > 1 && !_encProfile.CustomCommandLine.Contains("-8x8dct "))
                    if (!_encProfile.MacroBlocksPartitionsAdaptiveDct)
                        if (_encProfile.Preset > 0)
                            sb.Append("-8x8dct 0 ");

                // Trellis
                if (!_encProfile.CustomCommandLine.Contains("trellis="))
                {
                    display = false;
                    switch (_encProfile.Preset)
                    {
                        case 0:
                        case 1:
                        case 2: if (_encProfile.Trellis != 0) display = true; break;
                        case 3:
                        case 4:
                        case 5:
                        case 6: if (_encProfile.Trellis != 1) display = true; break;
                        case 7:
                        case 8:
                        case 9: if (_encProfile.Trellis != 2) display = true; break;
                    }
                    if (display)
                        x264Opts.Add(string.Format("trellis={0:0}", _encProfile.Trellis));
                }

                if (!_encProfile.CustomCommandLine.Contains("-psy-rd "))
                {
                    if (_encProfile.SubPixelRefinement > 5)
                    {
                        display = false;
                        switch (_encProfile.Tuning)
                        {
                            case 1: if ((Math.Abs(_encProfile.PsyRdStrength - 1.0F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.15F) > 0)) display = true; break;
                            case 2: if ((Math.Abs(_encProfile.PsyRdStrength - 0.4F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.0F) > 0)) display = true; break;
                            case 3: if ((Math.Abs(_encProfile.PsyRdStrength - 1.0F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.25F) > 0)) display = true; break;
                            case 7: if ((Math.Abs(_encProfile.PsyRdStrength - 1.0F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.2F) > 0)) display = true; break;
                            default: if ((Math.Abs(_encProfile.PsyRdStrength - 1.0F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.0F) > 0)) display = true; break;
                        }

                        if (display)
                            sb.AppendFormat(_appConfig.CInfo, "-psy 1 -psy-rd {0:0.00}:{1:0.00} ", _encProfile.PsyRdStrength, _encProfile.PsyTrellisStrength);
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("-mixed-refs"))
                    if (_encProfile.UseNoMixedReferenceFrames)
                        if (_encProfile.Preset >= 4) // preset fast
                            sb.Append("-mixed-refs 0 ");

                if (!_encProfile.CustomCommandLine.Contains("no-dct-decimate"))
                    if (_encProfile.UseNoDctDecimation)
                        if (_encProfile.Tuning != 3) // tune grain
                            x264Opts.Add("no-dct-decimate=1");

                if (!_encProfile.CustomCommandLine.Contains("-fast-pskip"))
                    if (_encProfile.UseNoFastPSkip)
                        if (_encProfile.Preset != 9) // preset placebo
                            sb.Append("-fast-pskip 0 ");


                _encProfile.UseAccessUnitDelimiters = GetAud(_encProfile, device);
                if (_encProfile.UseAccessUnitDelimiters && !_encProfile.UseBluRayCompatibility)
                    sb.Append("-aud 1 ");

                _encProfile.HrdInfo = GetNalHrd(_encProfile, device);
                switch (_encProfile.HrdInfo)
                {
                    case 1: if (!_encProfile.UseBluRayCompatibility) sb.Append("-nal-hrd vbr "); break;
                    case 2: sb.Append("-nal-hrd cbr "); break;
                }

                if (!_encProfile.CustomCommandLine.Contains("non-deterministic"))
                    if (_encProfile.UseNonDeterministic)
                        x264Opts.Add("non-deterministic");
                #endregion

                #region misc tab

                if (!_encProfile.CustomCommandLine.Contains("psnr"))
                    if (_encProfile.UsePsnrCalculation)
                        x264Opts.Add("psnr=1");

                if (!_encProfile.CustomCommandLine.Contains("-ssim"))
                    if (_encProfile.UseSsimCalculation)
                        sb.Append("-ssim 1 ");

                if (!_encProfile.CustomCommandLine.Contains("range="))
                    switch (_encProfile.VuiRange)
                    {
                        case 1:
                            x264Opts.Add("range=tv");
                            break;
                        case 2:
                            x264Opts.Add("range=pc");
                            break;
                    }

                #endregion

                #region ouput / custom

                var customSarValue = string.Empty;

                Dar? d = new Dar((ulong)hRes, (ulong)vRes);

                if (_encProfile.UseAutoSelectSar)
                {
                    var tempValue = GetSar(_encProfile, d, hRes, vRes, out customSarValue, String.Empty);
                    _encProfile.ForceSar = tempValue;
                }

                if (_encProfile.UseAutoSelectColorSettings)
                {
                    _encProfile.ColorPrimaries = GetColorprim(_encProfile, format);

                    _encProfile.Transfer = GetTransfer(_encProfile, format);

                    _encProfile.ColorMatrix = GetColorMatrix(_encProfile, format);
                }

                if (device.BluRay)
                {
                    if (_encProfile.InterlaceMode < 2)
                        _encProfile.InterlaceMode = GetInterlacedMode(format);

                    _encProfile.UseFakeInterlaced = GetFakeInterlaced(_encProfile, format, fpsN, fpsD);

                    _encProfile.UseForcePicStruct = GetPicStruct(_encProfile, format);

                    _encProfile.Pulldown = GetPulldown(_encProfile, format, fpsN, fpsD);
                }
                else
                {
                    if (_encProfile.InterlaceMode == 0)
                        _encProfile.InterlaceMode = GetInterlacedMode(format);

                    if (_encProfile.Pulldown == 0)
                        _encProfile.Pulldown = GetPulldown(_encProfile, format, fpsN, fpsD);
                }

                if (!_encProfile.CustomCommandLine.Contains("bff") &&
                    !_encProfile.CustomCommandLine.Contains("tff"))
                {
                    switch (_encProfile.InterlaceMode)
                    {
                        case 2: 
                            x264Opts.Add("bff=1");
                            flags.Add("+ildct");
                            break;
                        case 3: 
                            x264Opts.Add("tff=1");
                            flags.Add("+ildct");
                            break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("fake-interlaced="))
                {
                    if (_encProfile.UseFakeInterlaced && _encProfile.InterlaceMode == 1)
                        x264Opts.Add("fake-interlaced=1");
                }

                if (!_encProfile.CustomCommandLine.Contains("pic-struct="))
                {
                    if (_encProfile.UseForcePicStruct && _encProfile.InterlaceMode == 1 && _encProfile.Pulldown == 0)
                        x264Opts.Add("pic-struct=1");
                }

                if (!_encProfile.CustomCommandLine.Contains("colorprim="))
                {
                    switch (_encProfile.ColorPrimaries)
                    {
                        case 0: break;
                        case 1: x264Opts.Add("colorprim=bt709"); break;
                        case 2: x264Opts.Add("colorprim=bt470m"); break;
                        case 3: x264Opts.Add("colorprim=bt470bg"); break;
                        case 4: x264Opts.Add("colorprim=smpte170m"); break;
                        case 5: x264Opts.Add("colorprim=smpte240m"); break;
                        case 6: x264Opts.Add("colorprim=film"); break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("transfer="))
                {
                    switch (_encProfile.Transfer)
                    {
                        case 0: break;
                        case 1: x264Opts.Add("transfer=bt709"); break;
                        case 2: x264Opts.Add("transfer=bt470m"); break;
                        case 3: x264Opts.Add("transfer=bt470bg"); break;
                        case 4: x264Opts.Add("transfer=linear"); break;
                        case 5: x264Opts.Add("transfer=log100"); break;
                        case 6: x264Opts.Add("transfer=log316"); break;
                        case 7: x264Opts.Add("transfer=smpte170m"); break;
                        case 8: x264Opts.Add("transfer=smpte240m"); break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("colormatrix="))
                {
                    switch (_encProfile.ColorMatrix)
                    {
                        case 0: break;
                        case 1: x264Opts.Add("colormatrix=bt709"); break;
                        case 2: x264Opts.Add("colormatrix=fcc"); break;
                        case 3: x264Opts.Add("colormatrix=bt470bg"); break;
                        case 4: x264Opts.Add("colormatrix=smpte170m"); break;
                        case 5: x264Opts.Add("colormatrix=smpte240m"); break;
                        case 6: x264Opts.Add("colormatrix=GBR"); break;
                        case 7: x264Opts.Add("colormatrix=YCgCo"); break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("pulldown="))
                {
                    switch (_encProfile.Pulldown)
                    {
                        case 0: break;
                        case 1: break;
                        case 2: x264Opts.Add("pulldown=22"); break;
                        case 3: x264Opts.Add("pulldown=32"); break;
                        case 4: x264Opts.Add("pulldown=64"); break;
                        case 5: x264Opts.Add("pulldown=double"); break;
                        case 6: x264Opts.Add("pulldown=triple"); break;
                        case 7: x264Opts.Add("pulldown=euro"); break;
                    }
                }


                if (!String.IsNullOrEmpty(_encProfile.CustomCommandLine)) // add custom encoder options
                    sb.Append(Regex.Replace(_encProfile.CustomCommandLine, @"\r\n?|\n", string.Empty).Trim() + " ");

                if (!_encProfile.CustomCommandLine.Contains("sar="))
                {
                    switch (_encProfile.ForceSar)
                    {
                        case 0:
                            if (!String.IsNullOrEmpty(customSarValue))
                                sb.AppendFormat("-vf setsar={0} ", customSarValue);
                            break;
                        case 1:
                            sb.Append("-vf setsar=1/1 ");
                            break;
                        case 2:
                            sb.Append("-vf setsar=4/3 ");
                            break;
                        case 3:
                            sb.Append("-vf setsar=8/9 ");
                            break;
                        case 4:
                            sb.Append("-vf setsar=10/11 ");
                            break;
                        case 5:
                            sb.Append("-vf setsar=12/11 ");
                            break;
                        case 6:
                            sb.Append("-vf setsar=16/11 ");
                            break;
                        case 7:
                            sb.Append("-vf setsar=32/27 ");
                            break;
                        case 8:
                            sb.Append("-vf setsar=40/33 ");
                            break;
                        case 9:
                            sb.Append("-vf setsar=64/45 ");
                            break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("frame-packing="))
                {
                    if (stereo != StereoEncoding.None)
                        x264Opts.Add("frame-packing=3");
                }

                x264Opts.Add("force-cfr=1");

                if (flags.Count > 0)
                {
                    sb.Append("-flags ");
                    sb.Append(string.Join(",", flags));
                    sb.Append(" ");
                }

                if (x264Opts.Count > 0)
                {
                    sb.Append("-x264opts ");
                    sb.Append(string.Join(":", x264Opts));
                    sb.Append(" ");
                }

                //add the rest of the commandline regarding the output

                sb.Append("-bsf:v h264_mp4toannexb -y ");

                if ((_encProfile.EncodingMode == 2 || _encProfile.EncodingMode == 3) && (tempPass == 1))
                    sb.Append("-f h264 NUL ");
                else if (!String.IsNullOrEmpty(outFile))
                    sb.AppendFormat("\"{0}\" ", outFile);

                #endregion
            }
            return sb.ToString();
        }

        private int GetPulldown(X264Profile inProfile, VideoFormat format, int fpsN, int fpsD)
        {
            var pullDown = inProfile.Pulldown;

            switch (format)
            {
                case VideoFormat.Unknown:
                    break;
                case VideoFormat.Videoformat480I:
                    break;
                case VideoFormat.Videoformat480P:
                    pullDown = 3;
                    break;
                case VideoFormat.Videoformat576I:
                    break;
                case VideoFormat.Videoformat576P:
                    break;
                case VideoFormat.Videoformat720P:
                    if (((fpsN == 30000) && (fpsD == 1001)) || ((fpsN == 25000) && (fpsD == 1000))) // 29.976 or 25 fps
                        pullDown = 5;
                    break;
                case VideoFormat.Videoformat1080I:
                    break;
                case VideoFormat.Videoformat1080P:
                    break;
            }

            return pullDown;
        }

        private bool GetPicStruct(X264Profile inProfile, VideoFormat format)
        {
            var pStruct = inProfile.UseForcePicStruct;

            switch (format)
            {
                case VideoFormat.Videoformat576P:
                    pStruct = true;
                    break;
            }

            return pStruct;
        }

        private bool GetFakeInterlaced(X264Profile inProfile, VideoFormat format, int fpsN, int fpsD)
        {
            var fInterlaced = inProfile.UseFakeInterlaced;

            switch (format)
            {
                case VideoFormat.Videoformat480P:
                case VideoFormat.Videoformat576P:
                    fInterlaced = true;
                    break;
                case VideoFormat.Videoformat1080P:
                    if (((fpsN == 30000) && (fpsD == 1001)) || ((fpsN == 25000) && (fpsD == 1000))) // 29.976 or 25 fps
                        fInterlaced = true;
                    break;
            }

            return fInterlaced;
        }

        private int GetInterlacedMode(VideoFormat format)
        {
            int iMode;

            switch (format)
            {
                case VideoFormat.Videoformat480I:
                case VideoFormat.Videoformat576I:
                case VideoFormat.Videoformat1080I:
                    iMode = 2;
                    break;
                default:
                    iMode = 1;
                    break;
            }

            return iMode;
        }

        private int GetColorMatrix(X264Profile inProfile, VideoFormat format)
        {
            var matrix = inProfile.ColorMatrix;
            switch (format)
            {
                case VideoFormat.Videoformat480I:
                case VideoFormat.Videoformat480P:
                    matrix = 4;
                    break;
                case VideoFormat.Videoformat576I:
                case VideoFormat.Videoformat576P:
                    matrix = 3;
                    break;
                case VideoFormat.Videoformat720P:
                case VideoFormat.Videoformat1080I:
                case VideoFormat.Videoformat1080P:
                    matrix = 1;
                    break;
            }
            return matrix;
        }

        private int GetTransfer(X264Profile inProfile, VideoFormat format)
        {
            var transfer = inProfile.Transfer;
            switch (format)
            {
                case VideoFormat.Videoformat480I:
                case VideoFormat.Videoformat480P:
                    transfer = 7;
                    break;
                case VideoFormat.Videoformat576I:
                case VideoFormat.Videoformat576P:
                    transfer = 3;
                    break;
                case VideoFormat.Videoformat720P:
                case VideoFormat.Videoformat1080I:
                case VideoFormat.Videoformat1080P:
                    transfer = 1;
                    break;
            }
            return transfer;
        }

        private int GetColorprim(X264Profile inProfile, VideoFormat format)
        {
            var colorPrim = inProfile.ColorPrimaries;
            switch (format)
            {
                case VideoFormat.Videoformat480I:
                case VideoFormat.Videoformat480P:
                    colorPrim = 4;
                    break;
                case VideoFormat.Videoformat576I:
                case VideoFormat.Videoformat576P:
                    colorPrim = 3;
                    break;
                case VideoFormat.Videoformat720P:
                case VideoFormat.Videoformat1080I:
                case VideoFormat.Videoformat1080P:
                    colorPrim = 1;
                    break;
            }
            return colorPrim;
        }

        private int GetSar(X264Profile inProfile, Dar? d, int hRes, int vRes, out string customSarValue, string customSarValueInput)
        {
            var strCustomValue = string.Empty;
            var sar = inProfile.ForceSar;

            customSarValue = String.Empty;
            if (String.IsNullOrEmpty(customSarValueInput))
            {
                switch (strCustomValue.ToLower())
                {
                    case "1:1": sar = 1; break;
                    case "4:3": sar = 2; break;
                    case "8:9": sar = 3; break;
                    case "10:11": sar = 4; break;
                    case "12:11": sar = 5; break;
                    case "16:11": sar = 6; break;
                    case "32:27": sar = 7; break;
                    case "40:33": sar = 8; break;
                    case "64:45": sar = 9; break;
                    default:
                        customSarValue = strCustomValue;
                        sar = 0; break;
                }
            }

            if (d.HasValue && sar == 0 &&
                String.IsNullOrEmpty(customSarValue) && String.IsNullOrEmpty(customSarValueInput))
            {
                var s = d.Value.ToSar(hRes, vRes);
                switch (s.X + ":" + s.Y)
                {
                    case "1:1": sar = 1; break;
                    case "4:3": sar = 2; break;
                    case "8:9": sar = 3; break;
                    case "10:11": sar = 4; break;
                    case "12:11": sar = 5; break;
                    case "16:11": sar = 6; break;
                    case "32:27": sar = 7; break;
                    case "40:33": sar = 8; break;
                    case "64:45": sar = 9; break;
                    default: customSarValue = s.X + ":" + s.Y; break;
                }
            }

            return sar;
        }

        private int GetNalHrd(X264Profile inProfile, X264Device device)
        {
            var nalHrd = inProfile.HrdInfo;

            if (device.BluRay && nalHrd < 1)
            {
                nalHrd = 1;
            }

            return nalHrd;
        }

        private bool GetAud(X264Profile inProfile, X264Device device)
        {
            // TODO: need this one for resharper to suspend an false positive message
            // ReSharper disable ConditionIsAlwaysTrueOrFalse
            var aud = inProfile.UseAccessUnitDelimiters || device.BluRay && inProfile.UseAccessUnitDelimiters == false;
            // ReSharper restore ConditionIsAlwaysTrueOrFalse

            return aud;
        }

        private int GetVBVBufsize(X264Profile inProfile, X264Device device)
        {
            var vbvBufSize = inProfile.VbvBufSize;

            if (device.VbvBufsize > -1 && (vbvBufSize > device.VbvBufsize || vbvBufSize == 0))
            {
                vbvBufSize = device.VbvBufsize;
            }

            return vbvBufSize;
        }

        private int GetSlices(X264Profile inProfile, X264Device device)
        {
            var numSlices = inProfile.NumSlices;

            if (device.BluRay && numSlices != 4)
            {
                numSlices = 4;
            }

            return numSlices;
        }

        private int GetWeightp(X264Profile inProfile, X264Device device)
        {
            var weightP = inProfile.PFrameWeightedPrediction;

            if (device.BluRay && weightP > 1)
            {
                weightP = 1;
            }

            return weightP;
        }

        private int GetRefFrames(int hRes, int vRes, X264Profile inProfile, X264Device device)
        {
            var refFrames = inProfile.NumRefFrames;

            if (device.ReferenceFrames > -1 && refFrames > device.ReferenceFrames)
            {
                refFrames = device.ReferenceFrames;
            }

            var iMaxRefForLevel = X264Settings.GetMaxRefForLevel(inProfile.AvcLevel, hRes, vRes);
            if (iMaxRefForLevel > -1 && iMaxRefForLevel < refFrames)
            {
                refFrames = iMaxRefForLevel;
            }

            return refFrames;
        }

        private int GetBPyramid(X264Profile inProfile, X264Device device)
        {
            var bPyramid = inProfile.BPyramid;

            if (device.BluRay && inProfile.BPyramid > 1)
            {
                bPyramid = 1;
            }

            if (device.BPyramid > -1 && bPyramid != device.BPyramid)
            {
                bPyramid = device.BPyramid;
            }

            return bPyramid;
        }

        private int GetBFrames(X264Profile inProfile, X264Device device)
        {
            var numBframes = inProfile.NumBFrames;

            if (device.BFrames > -1 && inProfile.NumBFrames > device.BFrames)
            {
                numBframes = device.BFrames;
            }

            return numBframes;
        }

        private int GetMinKeyInt(int fpsN, int fpsD, int minGop, int maxGop, X264Device device, int gopCalculation)
        {
            var keyInt = 0;

            var fps = (double)fpsN / fpsD;
            if (gopCalculation == 1) // calculate min-keyint based on 25fps
                keyInt = (int)(minGop / 25.0 * fps);

            var maxValue = maxGop / 2 + 1;
            if (device.MaxGop > -1 && minGop > maxValue)
            {
                var Default = maxGop / 10;
                keyInt = Default;
            }

            return keyInt;
        }

        private int GetKeyInt(int fpsN, int fpsD, int maxGop, X264Device device, int gopCalculation)
        {
            var keyInt = 0;

            if (gopCalculation == 1)// calculate min-keyint based on 25fps
                keyInt = (int)Math.Round(maxGop / 25.0 * (fpsN / (double)fpsD), 0);

            var fps = (int)Math.Round((decimal)fpsN / fpsD, 0);

            if (device.MaxGop > -1 && maxGop > fps * device.MaxGop)
            {
                keyInt = fps * device.MaxGop;
            }

            return keyInt;
        }

        private int GetVBVMaxrate(X264Profile inProfile, X264Device device)
        {
            var vbvMaxRate = inProfile.VbvMaxRate;

            if (device.VbvMaxrate > -1 && (vbvMaxRate > device.VbvMaxrate || vbvMaxRate == 0))
            {
                vbvMaxRate = device.VbvMaxrate;
            }

            return vbvMaxRate;
        }

        #endregion
    }
}