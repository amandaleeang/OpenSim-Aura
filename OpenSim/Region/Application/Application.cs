/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Net;
using System.Reflection;
using log4net;
using log4net.Config;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;

namespace OpenSim
{
    /// <summary>
    /// Starting class for the OpenSimulator Region
    /// </summary>
    public class Application
    {
        /// <summary>
        /// Text Console Logger
        /// </summary>
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Path to the main ini Configuration file
        /// </summary>
        public static string iniFilePath = "";

        /// <summary>
        /// Save Crashes in the bin/crashes folder.  Configurable with m_crashDir
        /// </summary>
        public static bool m_saveCrashDumps = false;

        /// <summary>
        /// Directory to save crash reports to.  Relative to bin/
        /// </summary>
        public static string m_crashDir = "crashes";

        /// <summary>
        /// Instance of the OpenSim class.  This could be OpenSim or OpenSimBackground depending on the configuration
        /// </summary>
        protected static OpenSimBase m_sim = null;

        //could move our main function into OpenSimMain and kill this class
        public static void Main(string[] args)
        {
            // First line, hook the appdomain to the crash reporter
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            Culture.SetCurrentCulture();
            Culture.SetDefaultCurrentCulture();

            AppContext.SetSwitch("System.Drawing.EnableUnixSupport", true);

            /*
            // pre load System.Drawing.Common.dll for the platform
            // this will fail if a newer version is present on GAC, bin folder, etc, since LoadFrom only accepts the path, if it cannot find it elsewhere
            string targetdll = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"lib",
                        (Util.IsWindows() ? "win" : "linux"), "System.Drawing.Common.dll");
            try
            {
                Assembly asmb =  Assembly.LoadFrom(targetdll);
            }
            catch (Exception e)
            {
                m_log.Error("Failed to load System.Drawing.Common.dll for current platform" + e.Message);
                throw;
            }
            */
            string targetdll = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                        "System.Drawing.Common.dll");
            string src = targetdll + (Util.IsWindows() ? ".win" : ".linux");
            try
            {
                if (!File.Exists(targetdll))
                    File.Copy(src, targetdll);
                else
                {
                    FileInfo targetInfo = new(targetdll);
                    FileInfo srcInfo = new(src);
                    if(targetInfo.Length != srcInfo.Length)
                        File.Copy(src, targetdll, true);
                }
            }
            catch (Exception e)
            {
                m_log.Error("Failed to copy System.Drawing.Common.dll for current platform" + e.Message);
                throw;
            }

            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.MaxServicePointIdleTime = 30000;

            try { ServicePointManager.DnsRefreshTimeout = 5000; } catch { }
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            // Add the arguments supplied when running the application to the configuration
            ArgvConfigSource configSource = new ArgvConfigSource(args);

            // Configure Log4Net
            configSource.AddSwitch("Startup", "logconfig");
            string logConfigFile = configSource.Configs["Startup"].GetString("logconfig", string.Empty);
            if (!string.IsNullOrEmpty(logConfigFile))
            {
                XmlConfigurator.Configure(new System.IO.FileInfo(logConfigFile));
                m_log.Info($"[OPENSIM MAIN]: configured log4net using \"{logConfigFile}\" as configuration file");
            }
            else
            {
                XmlConfigurator.Configure(new System.IO.FileInfo("OpenSim.exe.config"));
                m_log.Info("[OPENSIM MAIN]: configured log4net using default OpenSim.exe.config");
            }

            m_log.Info($"[OPENSIM MAIN]: System Locale is {System.Threading.Thread.CurrentThread.CurrentCulture}");

            int workerThreadsMin = 500;
            int workerThreadsMax = 1000;
            int iocpThreadsMin = 1000;
            int iocpThreadsMax = 2000;

            System.Threading.ThreadPool.GetMinThreads(out int currentMinWorkerThreads, out int currentMinIocpThreads);
            m_log.Info(
                $"[OPENSIM MAIN]: Runtime gave us {currentMinWorkerThreads} min worker threads and {currentMinIocpThreads} min IOCP threads");

            System.Threading.ThreadPool.GetMaxThreads(out int workerThreads, out int iocpThreads);
            m_log.Info($"[OPENSIM MAIN]: Runtime gave us {workerThreads} max worker threads and {iocpThreads} max IOCP threads");

            if (workerThreads < workerThreadsMin)
            {
                workerThreads = workerThreadsMin;
                m_log.Info($"[OPENSIM MAIN]: Bumping up max worker threads to {workerThreads}");
            }
            if (workerThreads > workerThreadsMax)
            {
                workerThreads = workerThreadsMax;
                m_log.Info($"[OPENSIM MAIN]: Limiting max worker threads to {workerThreads}");
            }

            // Increase the number of IOCP threads available.
            // Mono defaults to a tragically low number (24 on 6-core / 8GB Fedora 17)
            if (iocpThreads < iocpThreadsMin)
            {
                iocpThreads = iocpThreadsMin;
                m_log.Info($"[OPENSIM MAIN]: Bumping up max IOCP threads to {iocpThreads}");
            }
            // Make sure we don't overallocate IOCP threads and thrash system resources
            if ( iocpThreads > iocpThreadsMax )
            {
                iocpThreads = iocpThreadsMax;
                m_log.Info($"[OPENSIM MAIN]: Limiting max IOCP completion threads to {iocpThreads}");
            }
            // set the resulting worker and IO completion thread counts back to ThreadPool
            if ( System.Threading.ThreadPool.SetMaxThreads(workerThreads, iocpThreads) )
            {
                m_log.Info(
                    $"[OPENSIM MAIN]: Threadpool set to {workerThreads} max worker threads and {iocpThreads} max IOCP threads");
            }
            else
            {
                m_log.Warn("[OPENSIM MAIN]: Threadpool reconfiguration failed, runtime defaults still in effect.");
            }

            // Check if the system is compatible with OpenSimulator.
            // Ensures that the minimum system requirements are met
            string error = string.Empty;
            if (Util.IsEnvironmentSupported(ref error))
            {
                m_log.Info("[OPENSIM MAIN]: Environment is supported by OpenSimulator.");
            }
            else
            {
                m_log.Warn($"[OPENSIM MAIN]: Environment is not supported by OpenSimulator: {error}\n");
            }

            m_log.Info($"Default culture changed to {Culture.GetDefaultCurrentCulture().DisplayName}");

            // First start: copy shipped examples when the live ini files are missing.
            // Existing files are left unchanged.
            CopyExampleConfigIfMissing("OpenSim.ini", "OpenSim.ini.example");
            CopyExampleConfigIfMissing(
                Path.Combine("config-include", "StandaloneCommon.ini"),
                Path.Combine("config-include", "StandaloneCommon.ini.example"));
            CopyExampleConfigIfMissing(
                Path.Combine("config-include", "GridCommon.ini"),
                Path.Combine("config-include", "GridCommon.ini.example"));
            CopyExampleConfigIfMissing(
                Path.Combine("config-include", "FlotsamCache.ini"),
                Path.Combine("config-include", "FlotsamCache.ini.example"));

            configSource.Alias.AddAlias("On", true);
            configSource.Alias.AddAlias("Off", false);
            configSource.Alias.AddAlias("True", true);
            configSource.Alias.AddAlias("False", false);
            configSource.Alias.AddAlias("Yes", true);
            configSource.Alias.AddAlias("No", false);

            configSource.AddSwitch("Startup", "background");
            configSource.AddSwitch("Startup", "inifile");
            configSource.AddSwitch("Startup", "inimaster");
            configSource.AddSwitch("Startup", "inidirectory");
            configSource.AddSwitch("Startup", "physics");
            configSource.AddSwitch("Startup", "gui");
            configSource.AddSwitch("Startup", "console");
            configSource.AddSwitch("Startup", "save_crashes");
            configSource.AddSwitch("Startup", "crash_dir");

            configSource.AddConfig("StandAlone");
            configSource.AddConfig("Network");

            // Check if we're running in the background or not
            bool background = configSource.Configs["Startup"].GetBoolean("background", false);

            // Check if we're saving crashes
            m_saveCrashDumps = configSource.Configs["Startup"].GetBoolean("save_crashes", false);

            // load Crash directory config
            m_crashDir = configSource.Configs["Startup"].GetString("crash_dir", m_crashDir);

            if (background)
            {
                m_sim = new OpenSimBackground(configSource);
                m_sim.Startup();
            }
            else
            {
                m_sim = new OpenSim(configSource);

                m_sim.Startup();

                while (true)
                {
                    try
                    {
                        // Block thread here for input
                        MainConsole.Instance.Prompt();
                    }
                    catch (Exception e)
                    {
                        m_log.Error($"Command error: {e}");
                    }
                }
            }
        }

        /// <summary>
        /// Copy dest from example when dest is missing. Never overwrites an existing file.
        /// </summary>
        static void CopyExampleConfigIfMissing(string destRelativePath, string exampleRelativePath)
        {
            string destPath = Path.Combine(Util.configDir(), destRelativePath);
            if (File.Exists(destPath))
                return;

            string examplePath = Path.Combine(Util.configDir(), exampleRelativePath);
            try
            {
                File.Copy(examplePath, destPath);
                m_log.Info($"[OPENSIM MAIN]: Copied {examplePath} to {destPath}");
            }
            catch (Exception e)
            {
                m_log.Error($"[OPENSIM MAIN]: Unable to copy {examplePath} to {destPath}: {e.Message}");
            }
        }

        private static bool _IsHandlingException = false; // Make sure we don't go recursive on ourself

        /// <summary>
        /// Global exception handler -- all unhandlet exceptions end up here :)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (_IsHandlingException)
            {
                return;
            }

            _IsHandlingException = true;
            // TODO: Add config option to allow users to turn off error reporting
            // TODO: Post error report (disabled for now)

            string msg = $"\r\nAPPLICATION EXCEPTION DETECTED: {e}\r\n\r\n";

            Exception ex = (Exception)e.ExceptionObject;

            msg += $"Exception: {ex}\r\n";
            if (ex.InnerException != null)
            {
                msg += $"InnerException: {ex.InnerException}\r\n";
            }

            msg += $"\r\nApplication is terminating: {e.IsTerminating}\r\n";

            m_log.Error("[APPLICATION]: + msg");

            if (m_saveCrashDumps)
            {
                // Log exception to disk
                try
                {
                    if (!Directory.Exists(m_crashDir))
                    {
                        Directory.CreateDirectory(m_crashDir);
                    }
                    string log = Util.GetUniqueFilename(ex.GetType() + ".txt");
                    using (StreamWriter m_crashLog = new StreamWriter(Path.Combine(m_crashDir, log)))
                    {
                        m_crashLog.WriteLine(msg);
                    }

                    File.Copy("OpenSim.ini", Path.Combine(m_crashDir, log + "_OpenSim.ini"), true);
                }
                catch (Exception e2)
                {
                    m_log.Error($"[CRASH LOGGER CRASHED]: {e2}");
                }
            }

            _IsHandlingException = false;
        }
    }
}
