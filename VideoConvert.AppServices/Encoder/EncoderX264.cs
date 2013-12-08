﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="EncoderX264.cs" company="JT-Soft (https://github.com/UniqProject/VideoConvert)">
//   This file is part of the VideoConvert.AppServices source code - It may be used under the terms of the GNU General Public License.
// </copyright>
// <summary>
//   
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace VideoConvert.AppServices.Encoder
{
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.IO.Pipes;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using DarLib;
    using Decoder;
    using Interfaces;
    using Interop.EventArgs;
    using Interop.Model;
    using Interop.Model.Profiles;
    using Interop.Model.x264;
    using Interop.Utilities;
    using log4net;
    using Services.Base;
    using Services.Interfaces;
    using Utilities;
    using ThreadState = System.Threading.ThreadState;

    /// <summary>
    /// The x264 encoder class
    /// </summary>
    public class EncoderX264 : EncodeBase, IEncoderX264
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(EncoderX264));

        #region Private Variables

        private static readonly string[] CLILevelNames =
        {
            "1", "1.1", "1.2", "1.3", "2", "2.1", "2.2", "3", "3.1",
            "3.2", "4", "4.1", "4.2", "5", "5.1"
        };

        private const string Executable = "x264.exe";
        private const string Executable64 = "x264_64.exe";

        private readonly Regex _frameInformation =
            new Regex(@"^\D?([\d]+).*frames: ([\d\.]+) fps, ([\d\.]+).*$",
                      RegexOptions.Singleline | RegexOptions.Multiline);

        private readonly Regex _fullFrameInformation =
            new Regex(@"^\[[\d\.]+?%\] ([\d]+?)/([\d]+?) frames, ([\d\.]+?) fps, ([\d\.]+?) kb/s.*$",
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

        private IAsyncResult _decodePipeState;

        private Thread _pipeReadThread;

        private X264Profile _encProfile;

        private string _outFile;

        private int _encodeMode;

        private long _frameCount;

        private TimeSpan _remainingTime;

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="EncoderX264"/> class.
        /// </summary>
        /// <param name="appConfig">
        /// The user Setting Service.
        /// </param>
        public EncoderX264(IAppConfigService appConfig) : base(appConfig)
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
            string verInfo = string.Empty;

            if (use64Bit && !Environment.Is64BitOperatingSystem) return string.Empty;

            string localExecutable = Path.Combine(encPath, use64Bit ? Executable64 : Executable);

            using (Process encoder = new Process())
            {
                ProcessStartInfo parameter = new ProcessStartInfo(localExecutable, "--version")
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
                    Log.ErrorFormat("x264 encoder exception: {0}", ex);
                }

                if (started)
                {
                    string output = encoder.StandardOutput.ReadToEnd();
                    Regex regObj = new Regex(@"^x264.+?(\d)\.(\d+)\.([\dM]+)",
                                             RegexOptions.Singleline | RegexOptions.Multiline);
                    Match result = regObj.Match(output);
                    if (result.Success)
                        verInfo = string.Format("Core: {0} Build {1}", result.Groups[2].Value, result.Groups[3].Value);

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
                Log.DebugFormat("x264 \"{0}\" found", verInfo);
            }
            return verInfo;
        }

        /// <summary>
        /// Execute a x264 process.
        /// This should only be called from the UI thread.
        /// </summary>
        /// <param name="encodeQueueTask">
        /// The encodeQueueTask.
        /// </param>
        public void Start(EncodeInfo encodeQueueTask)
        {
            try
            {
                if (this.IsEncoding)
                {
                    throw new Exception("x264 is already encoding.");
                }

                this.IsEncoding = true;
                this._currentTask = encodeQueueTask;

                bool use64BitEncoder = _appConfig.Use64BitEncoders &&
                                       _appConfig.X26464Installed &&
                                       Environment.Is64BitOperatingSystem;

                // TODO: this one is very ugly

                _encProfile = (X264Profile)this._currentTask.VideoProfile;

                if (!this._currentTask.EncodingProfile.Deinterlace && this._currentTask.VideoStream.Interlaced)
                    this._currentTask.VideoStream.Interlaced = false;

                Size resizeTo = VideoHelper.GetTargetSize(this._currentTask);

                if (string.IsNullOrEmpty(this._currentTask.AviSynthScript))
                {
                    AviSynthHelper avsHelper = new AviSynthHelper(this._appConfig);
                    avsHelper.GenerateAviSynthScript(this._currentTask, resizeTo);
                }

                string inputFile = this._currentTask.AviSynthScript;

                _outFile = FileSystemHelper.CreateTempFile(this._appConfig.DemuxLocation,
                    string.IsNullOrEmpty(this._currentTask.TempOutput) 
                        ? this._currentTask.BaseName 
                        : this._currentTask.TempOutput,
                    "encoded.264");

                int targetBitrate = 0;
                if (this._currentTask.EncodingProfile.TargetFileSize > 0)
                    targetBitrate = VideoHelper.CalculateVideoBitrate(this._currentTask);

                _encodeMode = this._encProfile.EncodingMode;
                _frameCount = this._currentTask.VideoStream.FrameCount;
                _encodePass = this._currentTask.StreamId;

                string x264CliPath = Path.Combine(this._appConfig.ToolsPath,
                                                  use64BitEncoder ? Executable64 : Executable);

                string query = GenerateCommandLine(targetBitrate,
                                                   resizeTo.Width,
                                                   resizeTo.Height,
                                                   this._encodePass,
                                                   this._currentTask.VideoStream.FrameRateEnumerator,
                                                   this._currentTask.VideoStream.FrameRateDenominator,
                                                   this._currentTask.EncodingProfile.StereoType,
                                                   this._currentTask.VideoStream.PicSize,
                                                   "-", 
                                                   _outFile);

                ProcessStartInfo cliStart = new ProcessStartInfo(x264CliPath, query)
                {
                    WorkingDirectory = this._appConfig.DemuxLocation,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = use64BitEncoder
                };

                this.EncodeProcess = new Process { StartInfo = cliStart };
                Log.InfoFormat("start parameter: x264 {0}", query);

                this._decodePipe = new NamedPipeServerStream(this._appConfig.DecodeNamedPipeName,
                                                             PipeDirection.InOut, 
                                                             3,
                                                             PipeTransmissionMode.Byte,
                                                             PipeOptions.Asynchronous);
                this._decodePipeState = this._decodePipe.BeginWaitForConnection(DecoderConnected, null);

                Size originalSize = new Size(this._currentTask.VideoStream.Width, this._currentTask.VideoStream.Height);
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

                this.EncodeProcess.ErrorDataReceived += X264ProcErrorDataReceived;
                this.EncodeProcess.BeginErrorReadLine();

                this.EncodeProcess.OutputDataReceived += X264ProcOutputDataReceived;
                this.EncodeProcess.BeginOutputReadLine();

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
            if (_decodePipe != null)
            {
                try
                {
                    _decodePipe.EndWaitForConnection(_decodePipeState);
                }
                catch (Exception exc)
                {
                    Log.Error(exc);
                }

                if(_pipeReadThread != null && _pipeReadThread.ThreadState == ThreadState.Running)
                    _pipeReadThread.Abort();

                if (_decodePipe.IsConnected)
                    _decodePipe.Disconnect();
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
            if (_pipeReadThread != null && _pipeReadThread.ThreadState == ThreadState.Running)
                _pipeReadThread.Abort();
            EncodeProcess.WaitForExit();

            try
            {
                this.EncodeProcess.CancelErrorRead();
                this.EncodeProcess.CancelOutputRead();
            }
            catch (Exception exc)
            {
                Log.Error(exc);
            }

            this._currentTask.ExitCode = EncodeProcess.ExitCode;
            Log.InfoFormat("Exit Code: {0:g}", this._currentTask.ExitCode);

            if (this._currentTask.ExitCode == 0)
            {
                if ((_encodeMode == 2 && this._encodePass == 2) ||
                    (_encodeMode == 3 && this._encodePass == 3) ||
                    (_encodeMode < 2 || this._encodePass > 3))
                {
                    this._currentTask.VideoStream.Encoded = true;
                    this._currentTask.VideoStream.IsRawStream = true;

                    this._currentTask.TempFiles.Add(this._currentTask.VideoStream.TempFile);
                    this._currentTask.VideoStream.TempFile = _outFile;

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
                    this._currentTask.TempFiles.Add(Path.Combine(_appConfig.DemuxLocation, "x264_2pass.log"));
                    this._currentTask.TempFiles.Add(Path.Combine(_appConfig.DemuxLocation, "x264_2pass.log.mbtree"));
                    this._currentTask.TempFiles.Add(this._currentTask.AviSynthScript);
                    this._currentTask.TempFiles.Add(this._currentTask.FfIndexFile);
                    this._currentTask.TempFiles.Add(this._currentTask.AviSynthStereoConfig);
                }
            }

            this._currentTask.CompletedStep = this._currentTask.NextStep;
            this.IsEncoding = false;
            this.InvokeEncodeCompleted(new EncodeCompletedEventArgs(true, null, string.Empty));
        }

        private void DecoderConnected(IAsyncResult ar)
        {
            Log.Info("Decoder Pipe connected");
            _decodePipeState = ar;
            _pipeReadThread = new Thread(PipeReadThreadStart);
            _pipeReadThread.Start();
            _pipeReadThread.Priority = _appConfig.GetThreadPriority();
        }

        private void PipeReadThreadStart()
        {
            try
            {
                if (EncodeProcess != null)
                    ReadThreadStart();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private void ReadThreadStart()
        {
            if (!_decodePipe.IsConnected)
            {
                _decodePipe.WaitForConnection();
            }

            try
            {
                _decodePipe.CopyTo(EncodeProcess.StandardInput.BaseStream);
            }
            catch (Exception exc)
            {
                Log.Error(exc);
            }
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
        private void X264ProcErrorDataReceived(object sender, DataReceivedEventArgs e)
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
        private void X264ProcOutputDataReceived(object sender, DataReceivedEventArgs e)
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

            Match frameMatch = _frameInformation.Match(line);
            Match fullFrameMatch = _fullFrameInformation.Match(line);

            TimeSpan eta = DateTime.Now.Subtract(_startTime);

            long current;
            long framesRemaining;
            long secRemaining;

            float encBitrate;
            float fps = 0f;
            float codingFPS = 0f;

            float percent = 0f;

            if (frameMatch.Success)
            {
                Int64.TryParse(frameMatch.Groups[1].Value, NumberStyles.Number,
                               _appConfig.CInfo, out current);
                framesRemaining = _frameCount - current;

                percent = ((float)current / _frameCount) * 100;

                if (eta.Seconds != 0) // prevent division by zero
                {
                    //Frames per Second
                    codingFPS = (float) Math.Round(current / eta.TotalSeconds, 2);
                }

                if (codingFPS > 1) // prevent another division by zero
                    secRemaining = framesRemaining / (int)codingFPS;
                else
                    secRemaining = 0;

                if (secRemaining > 0)
                    _remainingTime = new TimeSpan(0, 0, (int)secRemaining);

                Single.TryParse(frameMatch.Groups[2].Value, NumberStyles.Number,
                                _appConfig.CInfo, out fps);
                Single.TryParse(frameMatch.Groups[3].Value, NumberStyles.Number,
                                _appConfig.CInfo, out encBitrate);
            }
            else if (fullFrameMatch.Success)
            {
                Int64.TryParse(fullFrameMatch.Groups[1].Value, NumberStyles.Number,
                               _appConfig.CInfo, out current);
                Int64.TryParse(fullFrameMatch.Groups[2].Value, NumberStyles.Number,
                               _appConfig.CInfo, out _frameCount);

                framesRemaining = _frameCount - current;

                if (eta.Seconds != 0) // prevent division by zero
                {
                    //Frames per Second
                    codingFPS = (float) Math.Round(current / eta.TotalSeconds, 2);
                }

                if (codingFPS > 1) // prevent another division by zero
                    secRemaining = framesRemaining / (int)codingFPS;
                else
                    secRemaining = 0;

                if (secRemaining > 0)
                    _remainingTime = new TimeSpan(0, 0, (int)secRemaining);

                Single.TryParse(fullFrameMatch.Groups[3].Value, NumberStyles.Number,
                                _appConfig.CInfo, out fps);
                Single.TryParse(fullFrameMatch.Groups[4].Value, NumberStyles.Number,
                                _appConfig.CInfo, out encBitrate);
            }
            else
            {
                Log.InfoFormat("x264: {0}", line);
            }

            if (frameMatch.Success || fullFrameMatch.Success)
            {
                EncodeProgressEventArgs eventArgs = new EncodeProgressEventArgs
                {
                    AverageFrameRate = codingFPS,
                    CurrentFrameRate = fps,
                    EstimatedTimeLeft = _remainingTime,
                    PercentComplete = percent,
                    Task = 0,
                    TaskCount = 0,
                    ElapsedTime = DateTime.Now - this._startTime,
                };

                this.InvokeEncodeStatusChanged(eventArgs);
            }
        }

        private string GenerateCommandLine(int bitrate, int hRes, int vRes, int pass, int fpsN,
                                           int fpsD, StereoEncoding stereo = StereoEncoding.None, 
                                           VideoFormat format = VideoFormat.Unknown, string inFile = "input",
                                           string outFile = "output")
        {
            StringBuilder sb = new StringBuilder();
            if (_encProfile != null)
            {
                bool display;
                X264Device device = X264Device.CreateDeviceList()[_encProfile.TuneDevice];

                // AVC Profiles
                switch (_encProfile.AVCProfile)
                {
                    case 0:
                        sb.Append("--profile baseline ");
                        break;
                    case 1:
                        sb.Append("--profile main ");
                        break;
                    default:
                        sb.Append("--profile high ");
                        break;
                }

                // AVC Levels
                if (_encProfile.AVCLevel != 15) // unrestricted
                    sb.AppendFormat("--level {0} ", CLILevelNames[_encProfile.AVCLevel]);

                // Blu-Ray compatibility
                if (_encProfile.UseBluRayCompatibility)
                    sb.Append("--bluray-compat ");

                // x264 Presets
                if (!_encProfile.CustomCommandLine.Contains("--preset"))
                {
                    switch (_encProfile.Preset)
                    {
                        case 0: sb.Append("--preset ultrafast "); break;
                        case 1: sb.Append("--preset superfast "); break;
                        case 2: sb.Append("--preset veryfast "); break;
                        case 3: sb.Append("--preset faster "); break;
                        case 4: sb.Append("--preset fast "); break;
                        //case 5: sb.Append("--preset medium "); break; // default value
                        case 6: sb.Append("--preset slow "); break;
                        case 7: sb.Append("--preset slower "); break;
                        case 8: sb.Append("--preset veryslow "); break;
                        case 9: sb.Append("--preset placebo "); break;
                    }
                }

                // x264 Tunings
                if (!_encProfile.CustomCommandLine.Contains("--tune"))
                {
                    switch (_encProfile.Tuning)
                    {
                        case 1: sb.Append("--tune film "); break;
                        case 2: sb.Append("--tune animation "); break;
                        case 3: sb.Append("--tune grain "); break;
                        case 4: sb.Append("--tune psnr "); break;
                        case 5: sb.Append("--tune ssim "); break;
                        case 6: sb.Append("--tune fastdecode "); break;
                    }
                }

                // Encoding Modes
                int tempPass = pass;

                int tempBitrate = bitrate;
                int vbvBuf = GetVBVMaxrate(_encProfile, device);

                if (tempBitrate <= 0)
                    tempBitrate = _encProfile.VBRSetting;

                if (vbvBuf > 0 && tempBitrate > vbvBuf)   // limit Bitrate to max vbvbuf size
                    tempBitrate = vbvBuf;

                switch (_encProfile.EncodingMode)
                {
                    case 0: // ABR
                        if (!_encProfile.CustomCommandLine.Contains("--bitrate"))
                            sb.AppendFormat(_appConfig.CInfo, "--bitrate {0:0} ", tempBitrate);
                        break;
                    case 1: // Constant Quantizer
                        if (!_encProfile.CustomCommandLine.Contains("--qp"))
                            sb.AppendFormat(_appConfig.CInfo, "--qp {0:0}", _encProfile.QuantizerSetting);
                        break;
                    case 2: // automated 2 pass
                    case 3: // automated 3 pass
                        sb.AppendFormat(_appConfig.CInfo, "--pass {0:0} --bitrate {1:0} ", tempPass, tempBitrate);
                        break;
                    default:
                        if (!_encProfile.CustomCommandLine.Contains("--crf") && _encProfile.QualitySetting != 23)
                            sb.AppendFormat(_appConfig.CInfo, "--crf {0:0} ", _encProfile.QualitySetting);
                        break;
                }

                // Slow 1st Pass
                if (!_encProfile.CustomCommandLine.Contains("--slow-firstpass"))
                    if ((_encProfile.UseSlowFirstPass) && _encProfile.Preset < 9 && // 9 = placebo
                       ((_encProfile.EncodingMode == 2) || // automated twopass
                        (_encProfile.EncodingMode == 3)))  // automated threepass
                        sb.Append("--slow-firstpass ");

                // Threads
                if (!_encProfile.CustomCommandLine.Contains("--thread-input"))
                    if (_encProfile.UseThreadInput && _encProfile.NumThreads == 1)
                        sb.Append("--thread-input ");
                if (!_encProfile.CustomCommandLine.Contains("--threads"))
                    if (_encProfile.NumThreads > 0)
                        sb.AppendFormat(_appConfig.CInfo, "--threads {0:0} ", _encProfile.NumThreads);

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

                    if (!_encProfile.CustomCommandLine.Contains("--deblock "))
                        if (display)
                            sb.AppendFormat(_appConfig.CInfo, "--deblock {0:0}:{1:0} ", _encProfile.DeblockingStrength,
                                            _encProfile.DeblockingThreshold);
                }
                else
                {
                    if (!_encProfile.CustomCommandLine.Contains("--no-deblock"))
                        if (_encProfile.Preset != 0 && _encProfile.Tuning != 7) // ultrafast preset and not fast decode tuning
                            sb.Append("--no-deblock ");
                }

                if (_encProfile.AVCProfile > 0 && !_encProfile.CustomCommandLine.Contains("--no-cabac"))
                {
                    if (!_encProfile.UseCabac)
                    {
                        if (_encProfile.Preset != 0 && _encProfile.Tuning != 7) // ultrafast preset and not fast decode tuning
                            sb.Append("--no-cabac ");
                    }
                }

                // GOP Size
                int backupMaxGopSize = _encProfile.MaxGopSize;
                int backupMinGopSize = _encProfile.MinGopSize;

                _encProfile.MaxGopSize = GetKeyInt(fpsN, fpsD, backupMaxGopSize, device, _encProfile.GopCalculation);

                if (_encProfile.MaxGopSize != 250) // default size
                {
                    if (_encProfile.MaxGopSize == 0)
                        sb.Append("--keyint infinite ");
                    else
                        sb.AppendFormat(_appConfig.CInfo, "--keyint {0:0} ", _encProfile.MaxGopSize);
                }

                if (!_encProfile.UseBluRayCompatibility)
                {
                    _encProfile.MinGopSize = GetMinKeyInt(fpsN, fpsD, backupMinGopSize, _encProfile.MaxGopSize, device,
                                                        _encProfile.GopCalculation);
                    if (_encProfile.MinGopSize > (_encProfile.MaxGopSize / 2 + 1))
                    {
                        _encProfile.MinGopSize = _encProfile.MaxGopSize / 2 + 1;
                    }
                    int Default = Math.Min(_encProfile.MaxGopSize / 10, fpsN / fpsD);

                    if (_encProfile.MinGopSize != Default) // (MIN(--keyint / 10,--fps)) is default
                        sb.AppendFormat(_appConfig.CInfo, "--min-keyint {0:0} ", _encProfile.MinGopSize);
                }

                _encProfile.MaxGopSize = backupMaxGopSize;
                _encProfile.MinGopSize = backupMinGopSize;

                if (!_encProfile.CustomCommandLine.Contains("--open-gop") && (_encProfile.UseOpenGop || _encProfile.UseBluRayCompatibility))
                    sb.Append("--open-gop ");

                // B-Frames
                _encProfile.NumBFrames = GetBFrames(_encProfile, device);
                if (_encProfile.AVCProfile > 0 && _encProfile.NumBFrames != X264Settings.GetDefaultNumberOfBFrames(_encProfile.AVCLevel, _encProfile.Tuning, _encProfile.AVCProfile, device))
                    sb.AppendFormat(_appConfig.CInfo, "--bframes {0:0} ", _encProfile.NumBFrames);

                if (_encProfile.NumBFrames > 0)
                {
                    if (!_encProfile.CustomCommandLine.Contains("--b-adapt"))
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
                            sb.AppendFormat(_appConfig.CInfo, "--b-adapt {0:0} ", _encProfile.AdaptiveBFrames);
                    }

                    _encProfile.BPyramid = GetBPyramid(_encProfile, device);
                    if (_encProfile.NumBFrames > 1 && (_encProfile.BPyramid != 2 && !_encProfile.UseBluRayCompatibility || _encProfile.BPyramid != 1 && _encProfile.UseBluRayCompatibility))
                    {
                        switch (_encProfile.BPyramid) // pyramid needs a minimum of 2 b frames
                        {
                            case 1: sb.Append("--b-pyramid strict "); break;
                            case 0: sb.Append("--b-pyramid none "); break;
                        }
                    }

                    if (!_encProfile.CustomCommandLine.Contains("--no-weightb"))
                        if (!_encProfile.UseWeightedPred && _encProfile.Tuning != 7 && _encProfile.Preset != 0) // no weightpredb + tuning != fastdecode + preset != ultrafast
                            sb.Append("--no-weightb ");
                }

                // B-Frames bias
                if (!_encProfile.CustomCommandLine.Contains("--b-bias "))
                    if (_encProfile.BFrameBias != 0)
                        sb.AppendFormat(_appConfig.CInfo, "--b-bias {0:0} ", _encProfile.BFrameBias);


                // Other
                if (_encProfile.UseAdaptiveIFrameDecision)
                {
                    if (!_encProfile.CustomCommandLine.Contains("--scenecut "))
                        if (_encProfile.NumExtraIFrames != 40 && _encProfile.Preset != 0 ||
                            _encProfile.NumExtraIFrames != 0 && _encProfile.Preset == 0)
                            sb.AppendFormat(_appConfig.CInfo, "--scenecut {0:0} ", _encProfile.NumExtraIFrames);
                }
                else
                {
                    if (!_encProfile.CustomCommandLine.Contains("--no-scenecut"))
                        if (_encProfile.Preset != 0)
                            sb.Append("--no-scenecut ");
                }


                // reference frames
                int iRefFrames = GetRefFrames(hRes, vRes, _encProfile, device);
                if (iRefFrames != X264Settings.GetDefaultNumberOfRefFrames(_encProfile.Preset, _encProfile.Tuning, null, _encProfile.AVCLevel, hRes, vRes))
                    sb.AppendFormat(_appConfig.CInfo, "--ref {0:0} ", iRefFrames);

                // WeightedPPrediction
                _encProfile.PFrameWeightedPrediction = GetWeightp(_encProfile, device);
                if (_encProfile.PFrameWeightedPrediction != X264Settings.GetDefaultNumberOfWeightp(_encProfile.Preset,
                                                                                                 _encProfile.Tuning,
                                                                                                 _encProfile.AVCProfile,
                                                                                                 _encProfile.UseBluRayCompatibility))
                    sb.AppendFormat(_appConfig.CInfo, "--weightp {0:0} ", _encProfile.PFrameWeightedPrediction);

                // Slicing
                _encProfile.NumSlices = GetSlices(_encProfile, device);
                if (_encProfile.NumSlices != 0)
                    sb.AppendFormat(_appConfig.CInfo, "--slices {0:0} ", _encProfile.NumSlices);

                if (!_encProfile.CustomCommandLine.Contains("--slice-max-size "))
                    if (_encProfile.MaxSliceSizeBytes != 0)
                        sb.AppendFormat(_appConfig.CInfo, "--slice-max-size {0:0} ", _encProfile.MaxSliceSizeBytes);

                if (!_encProfile.CustomCommandLine.Contains("--slice-max-mbs "))
                    if (_encProfile.MaxSliceSizeBlocks != 0)
                        sb.AppendFormat(_appConfig.CInfo, "--slice-max-mbs {0:0} ", _encProfile.MaxSliceSizeBlocks);

                #endregion

                #region rc tab

                if (!_encProfile.CustomCommandLine.Contains("--qpmin"))
                    if (_encProfile.QuantizerMin != 0)
                        sb.AppendFormat(_appConfig.CInfo, "--qpmin {0:0} ", _encProfile.QuantizerMin);

                if (!_encProfile.CustomCommandLine.Contains("--qpmax"))
                    if (_encProfile.QuantizerMax != 69)
                        sb.AppendFormat(_appConfig.CInfo, "--qpmax {0:0} ", _encProfile.QuantizerMax);

                if (!_encProfile.CustomCommandLine.Contains("--qpstep"))
                    if (_encProfile.QuantizerDelta != 4)
                        sb.AppendFormat(_appConfig.CInfo, "--qpstep {0:0} ", _encProfile.QuantizerDelta);

                if (Math.Abs(_encProfile.QuantizerRatioIP - 1.4F) > 0)
                {
                    display = true;
                    if (_encProfile.Tuning == 3 && Math.Abs(_encProfile.QuantizerRatioIP - 1.1F) <= 0)
                        display = false;

                    if (!_encProfile.CustomCommandLine.Contains("--ipratio"))
                        if (display)
                            sb.AppendFormat(_appConfig.CInfo, "--ipratio {0:0} ", _encProfile.QuantizerRatioIP);
                }

                if (Math.Abs(_encProfile.QuantizerRatioPB - 1.3F) > 0)
                {
                    display = true;
                    if (_encProfile.Tuning == 3 && Math.Abs(_encProfile.QuantizerRatioPB - 1.1F) <= 0)
                        display = false;

                    if (!_encProfile.CustomCommandLine.Contains("--pbratio"))
                        if (display)
                            sb.AppendFormat(_appConfig.CInfo, "--pbratio {0:0} ", _encProfile.QuantizerRatioPB);
                }

                if (!_encProfile.CustomCommandLine.Contains("--chroma-qp-offset"))
                    if (_encProfile.ChromaQPOffset != 0)
                        sb.AppendFormat(_appConfig.CInfo, "--chroma-qp-offset {0:0} ", _encProfile.ChromaQPOffset);

                if (_encProfile.EncodingMode != 1) // doesn't apply to CQ mode
                {
                    _encProfile.VBVBufSize = GetVBVBufsize(_encProfile, device);
                    if (_encProfile.VBVBufSize > 0)
                        sb.AppendFormat(_appConfig.CInfo, "--vbv-bufsize {0:0} ", _encProfile.VBVBufSize);

                    _encProfile.VBVMaxRate = GetVBVMaxrate(_encProfile, device);
                    if (_encProfile.VBVMaxRate > 0)
                        sb.AppendFormat(_appConfig.CInfo, "--vbv-maxrate {0:0} ", _encProfile.VBVMaxRate);

                    if (!_encProfile.CustomCommandLine.Contains("--vbv-init"))
                        if (Math.Abs(_encProfile.VBVInitialBuffer - 0.9F) > 0)
                            sb.AppendFormat(_appConfig.CInfo, "--vbv-init {0:0.0} ", _encProfile.VBVInitialBuffer);

                    if (!_encProfile.CustomCommandLine.Contains("--ratetol"))
                        if (Math.Abs(_encProfile.BitrateVariance - 1.0F) > 0)
                            sb.AppendFormat(_appConfig.CInfo, "--ratetol {0:0.0} ", _encProfile.BitrateVariance);

                    if (!_encProfile.CustomCommandLine.Contains("--qcomp"))
                    {
                        display = true;
                        if ((_encProfile.Tuning == 3 && Math.Abs(_encProfile.QuantizerCompression - 0.8F) <= 0) || (_encProfile.Tuning != 3 && Math.Abs(_encProfile.QuantizerCompression - 0.6F) <= 0))
                            display = false;
                        if (display)
                            sb.AppendFormat(_appConfig.CInfo, "--qcomp {0:0.0} ", _encProfile.QuantizerCompression);
                    }

                    if (_encProfile.EncodingMode > 1) // applies only to twopass
                    {
                        if (!_encProfile.CustomCommandLine.Contains("--cplxblur"))
                            if (_encProfile.TempBlurFrameComplexity != 20)
                                sb.AppendFormat(_appConfig.CInfo, "--cplxblur {0:0} ", _encProfile.TempBlurFrameComplexity);

                        if (!_encProfile.CustomCommandLine.Contains("--qblur"))
                            if (Math.Abs(_encProfile.TempBlurQuant - 0.5F) > 0)
                                sb.AppendFormat(_appConfig.CInfo, "--qblur {0:0.0} ", _encProfile.TempBlurQuant);
                    }
                }

                // Dead Zones
                if (!_encProfile.CustomCommandLine.Contains("--deadzone-inter"))
                {
                    display = true;
                    if ((_encProfile.Tuning != 3 && _encProfile.DeadZoneInter == 21 && _encProfile.DeadZoneIntra == 11) ||
                        (_encProfile.Tuning == 3 && _encProfile.DeadZoneInter == 6 && _encProfile.DeadZoneIntra == 6))
                        display = false;
                    if (display)
                        sb.AppendFormat(_appConfig.CInfo, "--deadzone-inter {0:0} ", _encProfile.DeadZoneInter);
                }

                if (!_encProfile.CustomCommandLine.Contains("--deadzone-intra"))
                {
                    display = true;
                    if ((_encProfile.Tuning != 3 && _encProfile.DeadZoneIntra == 11) || (_encProfile.Tuning == 3 && _encProfile.DeadZoneIntra == 6))
                        display = false;
                    if (display)
                        sb.AppendFormat(_appConfig.CInfo, "--deadzone-intra {0:0} ", _encProfile.DeadZoneIntra);
                }

                // Disable Macroblok Tree
                if (!_encProfile.UseMBTree)
                {
                    if (!_encProfile.CustomCommandLine.Contains("--no-mbtree"))
                        if (_encProfile.Preset > 0) // preset veryfast
                            sb.Append("--no-mbtree ");
                }
                else
                {
                    // RC Lookahead
                    if (!_encProfile.CustomCommandLine.Contains("--rc-lookahead"))
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
                            sb.AppendFormat("--rc-lookahead {0:0} ", _encProfile.NumFramesLookahead);
                    }
                }

                // AQ-Mode
                if (_encProfile.EncodingMode != 1)
                {
                    if (!_encProfile.CustomCommandLine.Contains("--aq-mode"))
                    {
                        if (_encProfile.AdaptiveQuantizersMode != X264Settings.GetDefaultAQMode(_encProfile.Preset, _encProfile.Tuning))
                            sb.AppendFormat("--aq-mode {0:0} ", _encProfile.AdaptiveQuantizersMode);
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
                        if (!_encProfile.CustomCommandLine.Contains("--aq-strength"))
                            if (display)
                                sb.AppendFormat(_appConfig.CInfo, "--aq-strength {0:0.0} ", _encProfile.AdaptiveQuantizersStrength);
                    }
                }

                // custom matrices 
                if (_encProfile.AVCProfile > 1 && _encProfile.QuantizerMatrix > 0)
                {
                    switch (_encProfile.QuantizerMatrix)
                    {
                        case 1: if (!_encProfile.CustomCommandLine.Contains("--cqm")) sb.Append("--cqm \"jvt\" "); break;
                    }
                }
                #endregion

                #region analysis tab

                // Disable Chroma Motion Estimation
                if (!_encProfile.CustomCommandLine.Contains("--no-chroma-me"))
                    if (!_encProfile.UseChromaMotionEstimation)
                        sb.Append("--no-chroma-me ");

                // Motion Estimation Range
                if (!_encProfile.CustomCommandLine.Contains("--merange"))
                {
                    if ((_encProfile.Preset <= 7 && _encProfile.MotionEstimationRange != 16) ||
                        (_encProfile.Preset >= 8 && _encProfile.MotionEstimationRange != 24))
                        sb.AppendFormat("--merange {0:0} ", _encProfile.MotionEstimationRange);
                }

                // ME Type
                if (!_encProfile.CustomCommandLine.Contains("--me "))
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
                            case 0: sb.Append("--me dia "); break;
                            case 1: sb.Append("--me hex "); break;
                            case 2: sb.Append("--me umh "); break;
                            case 3: sb.Append("--me esa "); break;
                            case 4: sb.Append("--me tesa "); break;
                        }
                    }

                }

                if (!_encProfile.CustomCommandLine.Contains("--direct "))
                {
                    display = false;
                    if (_encProfile.Preset > 5) // preset medium
                    {
                        if (_encProfile.MVPredictionMod != 3)
                            display = true;
                    }
                    else if (_encProfile.MVPredictionMod != 1)
                        display = true;

                    if (display)
                    {
                        switch (_encProfile.MVPredictionMod)
                        {
                            case 0: sb.Append("--direct none "); break;
                            case 1: sb.Append("--direct spatial "); break;
                            case 2: sb.Append("--direct temporal "); break;
                            case 3: sb.Append("--direct auto "); break;
                        }
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("--nr "))
                    if (_encProfile.NoiseReduction > 0)
                        sb.AppendFormat("--nr {0:0} ", _encProfile.NoiseReduction);


                // subpel refinement
                if (!_encProfile.CustomCommandLine.Contains("--subme "))
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
                        sb.AppendFormat("--subme {0:0} ", _encProfile.SubPixelRefinement);
                }

                // macroblock types
                if (!_encProfile.CustomCommandLine.Contains("--partitions "))
                {
                    bool bExpectedP8X8Mv = true;
                    bool bExpectedB8X8Mv = true;
                    bool bExpectedI4X4Mv = true;
                    bool bExpectedI8X8Mv = true;
                    bool bExpectedP4X4Mv = true;

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

                    if (_encProfile.AVCProfile < 2)
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
                            sb.Append("--partitions ");
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
                            sb.Append("--partitions none ");
                    }
                }

                if (_encProfile.AVCProfile > 1 && !_encProfile.CustomCommandLine.Contains("--no-8x8dct"))
                    if (!_encProfile.MacroBlocksPartitionsAdaptiveDCT)
                        if (_encProfile.Preset > 0)
                            sb.Append("--no-8x8dct ");

                // Trellis
                if (!_encProfile.CustomCommandLine.Contains("--trellis "))
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
                        sb.AppendFormat("--trellis {0:0} ", _encProfile.Trellis);
                }

                if (!_encProfile.CustomCommandLine.Contains("--psy-rd "))
                {
                    if (_encProfile.SubPixelRefinement > 5)
                    {
                        display = false;
                        switch (_encProfile.Tuning)
                        {
                            case 1: if ((Math.Abs(_encProfile.PsyRDStrength - 1.0F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.15F) > 0)) display = true; break;
                            case 2: if ((Math.Abs(_encProfile.PsyRDStrength - 0.4F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.0F) > 0)) display = true; break;
                            case 3: if ((Math.Abs(_encProfile.PsyRDStrength - 1.0F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.25F) > 0)) display = true; break;
                            case 7: if ((Math.Abs(_encProfile.PsyRDStrength - 1.0F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.2F) > 0)) display = true; break;
                            default: if ((Math.Abs(_encProfile.PsyRDStrength - 1.0F) > 0) || (Math.Abs(_encProfile.PsyTrellisStrength - 0.0F) > 0)) display = true; break;
                        }

                        if (display)
                            sb.AppendFormat(_appConfig.CInfo, "--psy-rd {0:0.00}:{1:0.00} ", _encProfile.PsyRDStrength, _encProfile.PsyTrellisStrength);
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("--no-mixed-refs"))
                    if (_encProfile.UseNoMixedReferenceFrames)
                        if (_encProfile.Preset >= 4) // preset fast
                            sb.Append("--no-mixed-refs ");

                if (!_encProfile.CustomCommandLine.Contains("--no-dct-decimate"))
                    if (_encProfile.UseNoDCTDecimation)
                        if (_encProfile.Tuning != 3) // tune grain
                            sb.Append("--no-dct-decimate ");

                if (!_encProfile.CustomCommandLine.Contains("--no-fast-pskip"))
                    if (_encProfile.UseNoFastPSkip)
                        if (_encProfile.Preset != 9) // preset placebo
                            sb.Append("--no-fast-pskip ");

                if (!_encProfile.CustomCommandLine.Contains("--no-psy"))
                    if (_encProfile.UseNoPsychovisualEnhancements && (_encProfile.Tuning != 5 && _encProfile.Tuning != 6))
                        sb.Append("--no-psy ");

                _encProfile.UseAccessUnitDelimiters = GetAud(_encProfile, device);
                if (_encProfile.UseAccessUnitDelimiters && !_encProfile.UseBluRayCompatibility)
                    sb.Append("--aud ");

                _encProfile.HRDInfo = GetNalHrd(_encProfile, device);
                switch (_encProfile.HRDInfo)
                {
                    case 1: if (!_encProfile.UseBluRayCompatibility) sb.Append("--nal-hrd vbr "); break;
                    case 2: sb.Append("--nal-hrd cbr "); break;
                }

                if (!_encProfile.CustomCommandLine.Contains("--non-deterministic"))
                    if (_encProfile.UseNonDeterministic)
                        sb.Append("--non-deterministic ");
                #endregion

                #region misc tab

                if (!_encProfile.CustomCommandLine.Contains("--psnr"))
                    if (_encProfile.UsePSNRCalculation)
                        sb.Append("--psnr ");

                if (!_encProfile.CustomCommandLine.Contains("--ssim"))
                    if (_encProfile.UseSSIMCalculation)
                        sb.Append("--ssim ");

                if (!_encProfile.CustomCommandLine.Contains("--range "))
                    switch (_encProfile.VUIRange)
                    {
                        case 1:
                            sb.AppendFormat("--range tv ");
                            break;
                        case 2:
                            sb.Append("--range pc ");
                            break;
                    }

                #endregion

                #region input / ouput / custom

                string customSarValue = string.Empty;

                Dar? d = new Dar((ulong)hRes, (ulong)vRes);

                if (_encProfile.UseAutoSelectSAR)
                {
                    int tempValue = GetSar(_encProfile, d, hRes, vRes, out customSarValue, String.Empty);
                    _encProfile.ForceSAR = tempValue;
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

                if (!_encProfile.CustomCommandLine.Contains("--bff") &&
                    !_encProfile.CustomCommandLine.Contains("--tff"))
                {
                    switch (_encProfile.InterlaceMode)
                    {
                        case 2: sb.Append("--bff "); break;
                        case 3: sb.Append("--tff "); break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("--fake-interlaced"))
                {
                    if (_encProfile.UseFakeInterlaced && _encProfile.InterlaceMode == 1)
                        sb.Append("--fake-interlaced ");
                }

                if (!_encProfile.CustomCommandLine.Contains("--pic-struct"))
                {
                    if (_encProfile.UseForcePicStruct && _encProfile.InterlaceMode == 1 && _encProfile.Pulldown == 0)
                        sb.Append("--pic-struct ");
                }

                if (!_encProfile.CustomCommandLine.Contains("--colorprim"))
                {
                    switch (_encProfile.ColorPrimaries)
                    {
                        case 0: break;
                        case 1: sb.Append("--colorprim bt709 "); break;
                        case 2: sb.Append("--colorprim bt470m "); break;
                        case 3: sb.Append("--colorprim bt470bg "); break;
                        case 4: sb.Append("--colorprim smpte170m "); break;
                        case 5: sb.Append("--colorprim smpte240m "); break;
                        case 6: sb.Append("--colorprim film "); break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("--transfer"))
                {
                    switch (_encProfile.Transfer)
                    {
                        case 0: break;
                        case 1: sb.Append("--transfer bt709 "); break;
                        case 2: sb.Append("--transfer bt470m "); break;
                        case 3: sb.Append("--transfer bt470bg "); break;
                        case 4: sb.Append("--transfer linear "); break;
                        case 5: sb.Append("--transfer log100 "); break;
                        case 6: sb.Append("--transfer log316 "); break;
                        case 7: sb.Append("--transfer smpte170m "); break;
                        case 8: sb.Append("--transfer smpte240m "); break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("--colormatrix"))
                {
                    switch (_encProfile.ColorMatrix)
                    {
                        case 0: break;
                        case 1: sb.Append("--colormatrix bt709 "); break;
                        case 2: sb.Append("--colormatrix fcc "); break;
                        case 3: sb.Append("--colormatrix bt470bg "); break;
                        case 4: sb.Append("--colormatrix smpte170m "); break;
                        case 5: sb.Append("--colormatrix smpte240m "); break;
                        case 6: sb.Append("--colormatrix GBR "); break;
                        case 7: sb.Append("--colormatrix YCgCo "); break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("--pulldown"))
                {
                    switch (_encProfile.Pulldown)
                    {
                        case 0: break;
                        case 1: break;
                        case 2: sb.Append("--pulldown 22 "); break;
                        case 3: sb.Append("--pulldown 32 "); break;
                        case 4: sb.Append("--pulldown 64 "); break;
                        case 5: sb.Append("--pulldown double "); break;
                        case 6: sb.Append("--pulldown triple "); break;
                        case 7: sb.Append("--pulldown euro "); break;
                    }
                }


                if (!String.IsNullOrEmpty(_encProfile.CustomCommandLine)) // add custom encoder options
                    sb.Append(Regex.Replace(_encProfile.CustomCommandLine, @"\r\n?|\n", string.Empty).Trim() + " ");

                if (!_encProfile.CustomCommandLine.Contains("--sar"))
                {
                    switch (_encProfile.ForceSAR)
                    {
                        case 0:
                            {
                                if (!String.IsNullOrEmpty(customSarValue))
                                    sb.Append("--sar " + customSarValue + " ");
                                break;
                            }
                        case 1: sb.Append("--sar 1:1 "); break;
                        case 2: sb.Append("--sar 4:3 "); break;
                        case 3: sb.Append("--sar 8:9 "); break;
                        case 4: sb.Append("--sar 10:11 "); break;
                        case 5: sb.Append("--sar 12:11 "); break;
                        case 6: sb.Append("--sar 16:11 "); break;
                        case 7: sb.Append("--sar 32:27 "); break;
                        case 8: sb.Append("--sar 40:33 "); break;
                        case 9: sb.Append("--sar 64:45 "); break;
                    }
                }

                if (!_encProfile.CustomCommandLine.Contains("--frame-packing"))
                {
                    if (stereo != StereoEncoding.None)
                        sb.Append("--frame-packing 3 ");
                }

                //add the rest of the commandline regarding the output
                if ((_encProfile.EncodingMode == 2 || _encProfile.EncodingMode == 3) && (tempPass == 1))
                    sb.Append("--output NUL ");
                else if (!String.IsNullOrEmpty(outFile))
                    sb.AppendFormat("--output \"{0}\" ", outFile);

                if (!String.IsNullOrEmpty(inFile))
                {
                    if (String.CompareOrdinal(inFile, "-") == 0)
                        sb.AppendFormat("--demuxer y4m - ");
                    else
                        sb.AppendFormat("\"{0}\" ", inFile);
                }

                #endregion
            }
            return sb.ToString();
        }

        private int GetPulldown(X264Profile inProfile, VideoFormat format, int fpsN, int fpsD)
        {
            int pullDown = inProfile.Pulldown;

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
            bool pStruct = inProfile.UseForcePicStruct;

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
            bool fInterlaced = inProfile.UseFakeInterlaced;

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
            int matrix = inProfile.ColorMatrix;
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
            int transfer = inProfile.Transfer;
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
            int colorPrim = inProfile.ColorPrimaries;
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
            string strCustomValue = string.Empty;
            int sar = inProfile.ForceSAR;

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
                Sar s = d.Value.ToSar(hRes, vRes);
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
            int nalHrd = inProfile.HRDInfo;

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
            bool aud = inProfile.UseAccessUnitDelimiters || device.BluRay && inProfile.UseAccessUnitDelimiters == false;
            // ReSharper restore ConditionIsAlwaysTrueOrFalse

            return aud;
        }

        private int GetVBVBufsize(X264Profile inProfile, X264Device device)
        {
            int vbvBufSize = inProfile.VBVBufSize;

            if (device.VbvBufsize > -1 && (vbvBufSize > device.VbvBufsize || vbvBufSize == 0))
            {
                vbvBufSize = device.VbvBufsize;
            }

            return vbvBufSize;
        }

        private int GetSlices(X264Profile inProfile, X264Device device)
        {
            int numSlices = inProfile.NumSlices;

            if (device.BluRay && numSlices != 4)
            {
                numSlices = 4;
            }

            return numSlices;
        }

        private int GetWeightp(X264Profile inProfile, X264Device device)
        {
            int weightP = inProfile.PFrameWeightedPrediction;

            if (device.BluRay && weightP > 1)
            {
                weightP = 1;
            }

            return weightP;
        }

        private int GetRefFrames(int hRes, int vRes, X264Profile inProfile, X264Device device)
        {
            int refFrames = inProfile.NumRefFrames;

            if (device.ReferenceFrames > -1 && refFrames > device.ReferenceFrames)
            {
                refFrames = device.ReferenceFrames;
            }

            int iMaxRefForLevel = X264Settings.GetMaxRefForLevel(inProfile.AVCLevel, hRes, vRes);
            if (iMaxRefForLevel > -1 && iMaxRefForLevel < refFrames)
            {
                refFrames = iMaxRefForLevel;
            }

            return refFrames;
        }

        private int GetBPyramid(X264Profile inProfile, X264Device device)
        {
            int bPyramid = inProfile.BPyramid;

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
            int numBframes = inProfile.NumBFrames;

            if (device.BFrames > -1 && inProfile.NumBFrames > device.BFrames)
            {
                numBframes = device.BFrames;
            }

            return numBframes;
        }

        private int GetMinKeyInt(int fpsN, int fpsD, int minGop, int maxGop, X264Device device, int gopCalculation)
        {
            int keyInt = 0;

            double fps = (double)fpsN / fpsD;
            if (gopCalculation == 1) // calculate min-keyint based on 25fps
                keyInt = (int)(minGop / 25.0 * fps);

            int maxValue = maxGop / 2 + 1;
            if (device.MaxGop > -1 && minGop > maxValue)
            {
                int Default = maxGop / 10;
                keyInt = Default;
            }

            return keyInt;
        }

        private int GetKeyInt(int fpsN, int fpsD, int maxGop, X264Device device, int gopCalculation)
        {
            int keyInt = 0;

            if (gopCalculation == 1)// calculate min-keyint based on 25fps
                keyInt = (int)Math.Round(maxGop / 25.0 * (fpsN / (double)fpsD), 0);

            int fps = (int)Math.Round((decimal)fpsN / fpsD, 0);

            if (device.MaxGop > -1 && maxGop > fps * device.MaxGop)
            {
                keyInt = fps * device.MaxGop;
            }

            return keyInt;
        }

        private int GetVBVMaxrate(X264Profile inProfile, X264Device device)
        {
            int vbvMaxRate = inProfile.VBVMaxRate;

            if (device.VbvMaxrate > -1 && (vbvMaxRate > device.VbvMaxrate || vbvMaxRate == 0))
            {
                vbvMaxRate = device.VbvMaxrate;
            }

            return vbvMaxRate;
        }

        #endregion
    }
}