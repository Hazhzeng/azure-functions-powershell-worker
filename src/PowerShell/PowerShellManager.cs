//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

using Microsoft.Azure.Functions.PowerShellWorker.Utility;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using System.Management.Automation.Runspaces;
using LogLevel = Microsoft.Azure.WebJobs.Script.Grpc.Messages.RpcLog.Types.Level;

namespace Microsoft.Azure.Functions.PowerShellWorker.PowerShell
{
    using System.Management.Automation;

    internal class PowerShellManager
    {
        private readonly ILogger _logger;
        private readonly PowerShell _pwsh;
        private bool _runspaceInited;

        /// <summary>
        /// Gets the Runspace InstanceId.
        /// </summary>
        internal Guid InstanceId => _pwsh.Runspace.InstanceId;

        /// <summary>
        /// Gets the associated logger.
        /// </summary>
        internal ILogger Logger => _logger;

        static PowerShellManager()
        {
            // Set the type accelerators for 'HttpResponseContext' and 'HttpResponseContext'.
            // We probably will expose more public types from the worker in future for the interop between worker and the 'PowerShellWorker' module.
            // But it's most likely only 'HttpResponseContext' and 'HttpResponseContext' are supposed to be used directly by users, so we only add
            // type accelerators for these two explicitly.
            var accelerator = typeof(PSObject).Assembly.GetType("System.Management.Automation.TypeAccelerators");
            var addMethod = accelerator.GetMethod("Add", new Type[] { typeof(string), typeof(Type) });
            addMethod.Invoke(null, new object[] { "HttpResponseContext", typeof(HttpResponseContext) });
            addMethod.Invoke(null, new object[] { "HttpRequestContext", typeof(HttpRequestContext) });
        }

        /// <summary>
        /// Constructor for setting the basic fields.
        /// </summary>
        private PowerShellManager(ILogger logger, PowerShell pwsh, int id)
        {
            _logger = logger;
            _pwsh = pwsh;
            _pwsh.Runspace.Name = $"PowerShellManager{id}";
        }

        /// <summary>
        /// Create a PowerShellManager instance but defer the Initialization.
        /// </summary>
        /// <remarks>
        /// This constructor is only for creating the very first PowerShellManager instance.
        /// The initialization work is deferred until all prerequisites are ready, such as
        /// the dependent modules are downloaded and all Az functions are loaded.
        /// </remarks>
        internal PowerShellManager(ILogger logger, PowerShell pwsh)
            : this(logger, pwsh, id: 1)
        {
        }

        /// <summary>
        /// Create a PowerShellManager instance and initialize it.
        /// </summary>
        internal PowerShellManager(ILogger logger, int id)
            : this(logger, Utils.NewPwshInstance(), id)
        {
            // Initialize the Runspace
            Initialize();
        }

        /// <summary>
        /// Extra initialization of the Runspace.
        /// </summary>
        internal void Initialize()
        {
            if (!_runspaceInited)
            {
                RegisterStreamEvents();
                InvokeProfile(FunctionLoader.FunctionAppProfilePath);
                _runspaceInited = true;
            }
        }

        /// <summary>
        /// Setup Stream event listeners.
        /// </summary>
        private void RegisterStreamEvents()
        {
            var streamHandler = new StreamHandler(_logger);
            _pwsh.Streams.Debug.DataAdding += streamHandler.DebugDataAdding;
            _pwsh.Streams.Error.DataAdding += streamHandler.ErrorDataAdding;
            _pwsh.Streams.Information.DataAdding += streamHandler.InformationDataAdding;
            _pwsh.Streams.Progress.DataAdding += streamHandler.ProgressDataAdding;
            _pwsh.Streams.Verbose.DataAdding += streamHandler.VerboseDataAdding;
            _pwsh.Streams.Warning.DataAdding += streamHandler.WarningDataAdding;
        }

        /// <summary>
        /// This method invokes the FunctionApp's profile.ps1.
        /// </summary>
        internal void InvokeProfile(string profilePath)
        {
            Exception exception = null;
            if (profilePath == null)
            {
                string noProfileMsg = string.Format(PowerShellWorkerStrings.FileNotFound, "profile.ps1", FunctionLoader.FunctionAppRootPath);
                _logger.Log(LogLevel.Trace, noProfileMsg);
                return;
            }

            try
            {
                // Import-Module on a .ps1 file will evaluate the script in the global scope.
                _pwsh.AddCommand(Utils.ImportModuleCmdletInfo)
                        .AddParameter("Name", profilePath)
                        .AddParameter("PassThru", true)
                     .AddCommand(Utils.RemoveModuleCmdletInfo)
                        .AddParameter("Force", true)
                        .AddParameter("ErrorAction", "SilentlyContinue")
                     .InvokeAndClearCommands();
            }
            catch (Exception e)
            {
                exception = e;
                throw;
            }
            finally
            {
                if (_pwsh.HadErrors)
                {
                    string errorMsg = string.Format(PowerShellWorkerStrings.FailToRunProfile, profilePath);
                    _logger.Log(LogLevel.Error, errorMsg, exception, isUserLog: true);
                }
            }
        }

        /// <summary>
        /// Execution a function fired by a trigger or an activity function scheduled by an orchestration.
        /// </summary>
        internal Hashtable InvokeFunction(
            AzFunctionInfo functionInfo,
            Hashtable triggerMetadata,
            IList<ParameterBinding> inputData)
        {
            string scriptPath = functionInfo.ScriptPath;
            string entryPoint = functionInfo.EntryPoint;
            string moduleName = null;

            try
            {
                if (string.IsNullOrEmpty(entryPoint))
                {
                    _pwsh.AddCommand(scriptPath);
                }
                else
                {
                    // If an entry point is defined, we import the script module.
                    moduleName = Path.GetFileNameWithoutExtension(scriptPath);
                    _pwsh.AddCommand(Utils.ImportModuleCmdletInfo)
                            .AddParameter("Name", scriptPath)
                         .InvokeAndClearCommands();

                    _pwsh.AddCommand(entryPoint);
                }

                // Set arguments for each input binding parameter
                foreach (ParameterBinding binding in inputData)
                {
                    string bindingName = binding.Name;
                    if (functionInfo.FuncParameters.TryGetValue(bindingName, out PSScriptParamInfo paramInfo))
                    {
                        var bindingInfo = functionInfo.InputBindings[bindingName];
                        var valueToUse = Utils.TransformInBindingValueAsNeeded(paramInfo, bindingInfo, binding.Data.ToObject());
                        _pwsh.AddParameter(bindingName, valueToUse);
                    }
                }

                // Gives access to additional Trigger Metadata if the user specifies TriggerMetadata
                if(functionInfo.HasTriggerMetadataParam)
                {
                    _pwsh.AddParameter(AzFunctionInfo.TriggerMetadata, triggerMetadata);
                }

                Collection<object> pipelineItems = _pwsh.AddCommand("Microsoft.Azure.Functions.PowerShellWorker\\Trace-PipelineObject")
                                                        .InvokeAndClearCommands<object>();

                Hashtable result = _pwsh.AddCommand("Microsoft.Azure.Functions.PowerShellWorker\\Get-OutputBinding")
                                            .AddParameter("Purge", true)
                                        .InvokeAndClearCommands<Hashtable>()[0];

                /*
                 * TODO: See GitHub issue #82. We are not settled on how to handle the Azure Functions concept of the $returns Output Binding
                if (pipelineItems != null && pipelineItems.Count > 0)
                {
                    // If we would like to support Option 1 from #82, use the following 3 lines of code:                    
                    object[] items = new object[pipelineItems.Count];
                    pipelineItems.CopyTo(items, 0);
                    result.Add(AzFunctionInfo.DollarReturn, items);

                    // If we would like to support Option 2 from #82, use this line:
                    result.Add(AzFunctionInfo.DollarReturn, pipelineItems[pipelineItems.Count - 1]);
                }
                */

                return result;
            }
            finally
            {
                ResetRunspace(moduleName);
            }
        }

        private void ResetRunspace(string moduleName)
        {
            // Reset the runspace to the Initial Session State
            _pwsh.Runspace.ResetRunspaceState();

            if (!string.IsNullOrEmpty(moduleName))
            {
                // If the function had an entry point, this will remove the module that was loaded
                _pwsh.AddCommand(Utils.RemoveModuleCmdletInfo)
                        .AddParameter("Name", moduleName)
                        .AddParameter("Force", true)
                        .AddParameter("ErrorAction", "SilentlyContinue")
                     .InvokeAndClearCommands();
            }

            // Clean up jobs started during the function execution.
            _pwsh.AddCommand(Utils.GetJobCmdletInfo)
                 .AddCommand(Utils.RemoveJobCmdletInfo)
                    .AddParameter("Force", true)
                    .AddParameter("ErrorAction", "SilentlyContinue")
                 .InvokeAndClearCommands();
        }
    }
}
