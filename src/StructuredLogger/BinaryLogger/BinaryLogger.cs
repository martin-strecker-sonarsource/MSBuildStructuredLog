﻿using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Build.Framework;

namespace Microsoft.Build.Logging
{
    /// <summary>
    /// A logger that serializes all incoming BuildEventArgs in a compressed binary file (*.binlog). The file
    /// can later be played back and piped into other loggers (file, console, etc) to reconstruct the log contents
    /// as if a real build was happening. Additionally, this format can be read by tools for
    /// analysis or visualization. Since the file format preserves structure, tools don't have to parse
    /// text logs that erase a lot of useful information.
    /// </summary>
    /// <remarks>The logger is public so that it can be instantiated from MSBuild.exe via command-line switch.</remarks>
    public sealed class BinaryLogger : ILogger
    {
        internal const int FileFormatVersion = 2;

        private Stream stream;
        private BinaryWriter binaryWriter;
        private BuildEventArgsWriter eventArgsWriter;
        private ProjectImportsCollector projectImportsCollector;

        private string FilePath { get; set; }

        /// <summary>
        /// Describes whether to capture the project and target source files used during the build.
        /// If the source files are captured, they can be embedded in the log file or as a separate zip archive.
        /// </summary>
        public enum ProjectImportsCollectionMode
        {
            /// <summary>
            /// Don't capture the source files during the build.
            /// </summary>
            None,

            /// <summary>
            /// Embed the source files directly in the log file.
            /// </summary>
            Embed,

            /// <summary>
            /// Create an external .buildsources.zip archive for the files.
            /// </summary>
            ZipFile
        }

        public ProjectImportsCollectionMode CollectProjectImports { get; set; } = ProjectImportsCollectionMode.Embed;

        /// <summary>
        /// The binary logger Verbosity is always maximum (Diagnostic). It tries to capture as much
        /// information as possible.
        /// </summary>
        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;

        /// <summary>
        /// The only supported parameter is the output log file path (e.g. "msbuild.binlog") 
        /// </summary>
        public string Parameters { get; set; }

        /// <summary>
        /// Initializes the logger by subscribing to events of IEventSource
        /// </summary>
        public void Initialize(IEventSource eventSource)
        {
            Environment.SetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING", "true");

            ProcessParameters();

            try
            {
                stream = new FileStream(FilePath, FileMode.Create);

                if (CollectProjectImports != ProjectImportsCollectionMode.None)
                {
                    projectImportsCollector = new ProjectImportsCollector(FilePath);
                }
            }
            catch (Exception e)
            {
                string errorCode = "";
                string helpKeyword = "";
                string message = e.Message;
                throw new LoggerException(message, e, errorCode, helpKeyword);
            }

            stream = new GZipStream(stream, CompressionLevel.Optimal);
            binaryWriter = new BinaryWriter(stream);
            eventArgsWriter = new BuildEventArgsWriter(binaryWriter);

            binaryWriter.Write(FileFormatVersion);

            eventSource.AnyEventRaised += EventSource_AnyEventRaised;
        }

        /// <summary>
        /// Closes the underlying file stream.
        /// </summary>
        public void Shutdown()
        {
            if (projectImportsCollector != null)
            {
                projectImportsCollector.Close();

                if (CollectProjectImports == ProjectImportsCollectionMode.Embed)
                {
                    var archiveFilePath = projectImportsCollector.ArchiveFilePath;

                    // It is possible that the archive couldn't be created for some reason.
                    // Only embed it if it actually exists.
                    if (File.Exists(archiveFilePath))
                    {
                        eventArgsWriter.WriteBlob(BinaryLogRecordKind.ProjectImportArchive, File.ReadAllBytes(archiveFilePath));
                        File.Delete(archiveFilePath);
                    }
                }

                projectImportsCollector = null;
            }

            if (stream != null)
            {
                // It's hard to determine whether we're at the end of decoding GZipStream
                // so add an explicit 0 at the end to signify end of file
                stream.WriteByte((byte)BinaryLogRecordKind.EndOfFile);
                stream.Flush();
                stream.Dispose();
                stream = null;
            }
        }

        private void EventSource_AnyEventRaised(object sender, BuildEventArgs e)
        {
            Write(e);
        }

        private void Write(BuildEventArgs e)
        {
            if (stream != null)
            {
                // TODO: think about queuing to avoid contention
                lock (eventArgsWriter)
                {
                    eventArgsWriter.Write(e);
                }

                if (projectImportsCollector != null)
                {
                    projectImportsCollector.IncludeSourceFiles(e);
                }
            }
        }

        /// <summary>
        /// Processes the parameters given to the logger from MSBuild.
        /// </summary>
        /// <exception cref="LoggerException">
        /// </exception>
        private void ProcessParameters()
        {
            const string invalidParamSpecificationMessage = @"Need to specify a valid log file name, such as msbuild.binlog";

            if (Parameters == null)
            {
                throw new LoggerException(invalidParamSpecificationMessage);
            }

            string[] parameters = Parameters.Split(';');

            if (parameters.Length != 1)
            {
                throw new LoggerException(invalidParamSpecificationMessage);
            }

            FilePath = parameters[0].TrimStart('"').TrimEnd('"');

            try
            {
                FilePath = Path.GetFullPath(FilePath);
            }
            catch (Exception e)
            {
                string errorCode = "";
                string helpKeyword = "";
                string message = e.Message;
                throw new LoggerException(message, e, errorCode, helpKeyword);
            }
        }
    }
}
