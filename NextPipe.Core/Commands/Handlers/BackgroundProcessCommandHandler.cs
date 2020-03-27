using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NextPipe.Core.Commands.Commands.ProcessLockCommands;
using NextPipe.Core.Domain.NextPipeTask.ValueObject;
using NextPipe.Core.Domain.SharedValueObjects;
using NextPipe.Core.Events.Events;
using NextPipe.Core.Events.Events.ModuleEvents;
using NextPipe.Core.Helpers;
using NextPipe.Core.Kubernetes;
using NextPipe.Messaging.Infrastructure.Contracts;
using NextPipe.Persistence.Entities.ProcessLock;
using NextPipe.Persistence.Repositories;
using NextPipe.Utilities.Documents.Responses;
using SimpleSoft.Mediator;

namespace NextPipe.Core.Commands.Handlers
{
    public class BackgroundProcessCommandHandler : CommandHandlerBase,
        ICommandHandler<CleanupHangingTasksCommand, Response>,
        ICommandHandler<InstallPendingModulesCommand, Response>
    {
        private readonly IProcessLockRepository _processLockRepository;
        private readonly IKubectlHelper _kubectlHelper;
        private const string NEXTPIPE_DEPLOYMENT_NAME = "nextpipe-deployment";

        public BackgroundProcessCommandHandler(IEventPublisher eventPublisher, IProcessLockRepository processLockRepository, IKubectlHelper kubectlHelper) : base(eventPublisher)
        {
            _processLockRepository = processLockRepository;
            _kubectlHelper = kubectlHelper;
        }

        /// <summary>
        /// Request a processLock for the respective host
        /// </summary>
        /// <param name="cmd"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<Response> HandleAsync(CleanupHangingTasksCommand cmd, CancellationToken ct)
        {
            return await InitiateLongRunningProcess(NextPipeProcessType.CleanUpHangingTasks, nameof(CleanupHangingTasksCommand), async () =>
            {
                await _eventPublisher.PublishAsync(new CleanupHangingTasksEvent(), ct);
            });
        }
        
        public async Task<Response> HandleAsync(InstallPendingModulesCommand cmd, CancellationToken ct)
        {
            return await InitiateLongRunningProcess(NextPipeProcessType.InstallPendingModulesTasks, nameof(InstallPendingModulesCommand), async () =>
                {
                    await _eventPublisher.PublishAsync(new InstallPendingModulesEvent(), ct);
                });
        }

        private async Task<Response> InitiateLongRunningProcess(NextPipeProcessType processType, string cmdName, Func<Task> func)
        {
            // Check if there exists a Process of respective type which is already running...
            LogHandler.WriteLineVerbose($"Request processLock for processType: {processType}");
            var processLock = await RequestProcessLock(processType);

            if (processLock == null)
            {
                // Request for process lock was not successful return unsuccesfull cmd and try new cleanup in 30 secs
                LogHandler.WriteLineVerbose($"Couldn't receive processLock for type:{processType}, waiting for next {processType} session");
                return Response.Unsuccessful();
            }
            
            LogHandler.WriteLineVerbose($"ProcessLock of type: {processType} received for cmd: {cmdName}");

            await func();

            LogHandler.WriteLineVerbose($"{processType} process done, deleting processLock with id: {processLock.Id}");
            // The process is done, remove the processLock
            await _processLockRepository.Delete(processLock.Id);
            
            return Response.Success();
        }


        /// <summary>
        /// Returns null if the method was not able to assign a processLock. Else returns a processLock
        /// </summary>
        /// <param name="processType"></param>
        /// <returns></returns>
        private async Task<ProcessLock> RequestProcessLock(NextPipeProcessType processType)
        {
            // Find process of processType
            var process =
                await _processLockRepository.FindProcessLockByProcessType(processType);

            if (process != null)
            {
                LogHandler.WriteLineVerbose($"Process already running on host: {process.Hostname}");
                // The process is already running - Make sure that the processLock is not assigned to a dead host
                var hostPods =
                    await _kubectlHelper.GetPodsByCustomNameFilter(NEXTPIPE_DEPLOYMENT_NAME,
                        ShellHelper.IdenticalStart);

                LogHandler.WriteLineVerbose("Running hosts");
                foreach (var hostPod in hostPods)
                {
                    Console.WriteLine($"- {hostPod.Metadata.Name}");
                }

                if (!hostPods.Any(t => t.Metadata.Name.Trim().ToLower().Equals(process.Hostname.Trim().ToLower())))
                {
                    LogHandler.WriteLineVerbose(
                        $"Process was hanging on dead host: {process.Hostname}. Rescheduling the process to host: {new Hostname().Value}");
                    // The hostname of the process does not match any of the current hosts
                    // Re-schedule the CleanupHangingTaskCommand by deleting and inserting a new processLock
                    // Attached to this host
                    return await _processLockRepository.ReplaceProcessLock(new ProcessLock
                    {
                        Hostname = new Hostname().Value,
                        Id = new Id().Value,
                        ProcessId = new Id().Value,
                        NextPipeProcessType = NextPipeProcessType.CleanUpHangingTasks
                    }, process);
                }
            }
            else
            {
                LogHandler.WriteLineVerbose("No process was running trying to request processLock");
                // The process is not running, create a processLock for this host
                // This might fail if another replica beats us to the finish line
                return await _processLockRepository.InsertAndReturn(new ProcessLock
                {
                    Hostname = new Hostname().Value,
                    Id = new Id().Value,
                    ProcessId = new Id().Value,
                    NextPipeProcessType = NextPipeProcessType.CleanUpHangingTasks
                });
            }

            return null;
        }
    }
}