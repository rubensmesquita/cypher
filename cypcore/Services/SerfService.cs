﻿// CYPCore by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Serilog;
using CliWrap;
using CliWrap.EventStream;
using CYPCore.Serf;
using CYPCore.Models;
using CYPCore.Cryptography;
using System.Runtime.InteropServices;
using CYPCore.Extensions;
using Microsoft.Extensions.Hosting;

namespace CYPCore.Services
{
    public interface ISerfService : IStartable
    {
        Task StartAsync(IHostApplicationLifetime applicationLifetime);
        Task<bool> JoinSeedNodes(SeedNode seedNode);
        bool JoinedSeedNodes { get; }
        bool Disabled { get; }
    }

    public class SerfService : ISerfService
    {
        private readonly ISerfClient _serfClient;
        private readonly ISigning _signing;
        private readonly ILogger _logger;
        private readonly TcpSession _tcpSession;

        public bool Disabled { get; private set; }

        public SerfService(ISerfClient serfClient, ISigning signing, ILogger logger)
        {
            _serfClient = serfClient;
            _signing = signing;
            _logger = logger.ForContext("SourceContext", nameof(SerfService));

            _tcpSession = _serfClient.TcpSessionsAddOrUpdate(new TcpSession(
                serfClient.SerfConfigurationOptions.Listening).Connect(_serfClient.SerfConfigurationOptions.RPC));
        }

        /// <summary>
        /// 
        /// </summary>
        public void Start()
        {
            // Empty
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="applicationLifetime"></param>
        /// <returns></returns>
        public async Task StartAsync(IHostApplicationLifetime applicationLifetime)
        {
            if (_serfClient.ProcessStarted)
                return;

            if (_serfClient.SerfConfigurationOptions.Disabled)
            {
                Disabled = true;
                return;
            }

            if (IsRunning())
            {
                _logger.Here().Warning("Serf is already running. It's OK if you are running on a different port.");
            }

            var useExisting = await TryUseExisting();
            if (useExisting)
            {
                _serfClient.ProcessStarted = true;
                _logger.Here().Information("Process Id cannot be found at this moment.");
                return;
            }

            try
            {
                applicationLifetime.ApplicationStopping.Register(() =>
                {
                    try
                    {
                        var process = Process.GetProcessById(_serfClient.ProcessId);
                        process?.Kill();
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                });

                var pubKey = await _signing.GetPublicKey(_signing.DefaultSigningKeyName);

                _serfClient.Name = $"{_serfClient.SerfConfigurationOptions.NodeName}-{Helper.Util.Sha384ManagedHash(pubKey).ByteToHex().Substring(0, 16)}";

                var serfPath = GetFilePath();

                _logger.Here().Information("Serf assembly path: {@SerfPath}", serfPath);

                //  Chmod before attempting to execute serf on Linux and Mac
                if (new[] { OSPlatform.Linux, OSPlatform.OSX }.Contains(Helper.Util.GetOperatingSystemPlatform()))
                {
                    _logger.Here().Information("Granting execute permission on serf assembly");

                    var chmodCmd = Cli.Wrap("chmod")
                       .WithArguments(a => a
                       .Add("+x")
                       .Add(serfPath));

                    await chmodCmd.ExecuteAsync();
                }

                var cmd = Cli.Wrap(serfPath)
                    .WithArguments(a => a
                    .Add("agent")
                    .Add($"-bind={_serfClient.SerfConfigurationOptions.Listening}")
                    .Add($"-rpc-addr={_serfClient.SerfConfigurationOptions.RPC}")
                    .Add($"-advertise={_serfClient.SerfConfigurationOptions.Advertise}")
                    .Add($"-encrypt={ _serfClient.SerfConfigurationOptions.Encrypt}")
                    .Add($"-node={_serfClient.Name}")
                    .Add($"-snapshot={_serfClient.SerfConfigurationOptions.SnapshotPath}")
                    .Add($"-rejoin={_serfClient.SerfConfigurationOptions.Rejoin}")
                    .Add($"-broadcast-timeout={_serfClient.SerfConfigurationOptions.BroadcastTimeout}")
                    .Add($"-retry-max={_serfClient.SerfConfigurationOptions.RetryMax}")
                    .Add($"-log-level={_serfClient.SerfConfigurationOptions.Loglevel}")
                    .Add($"-profile={_serfClient.SerfConfigurationOptions.Profile}")
                    .Add("-tag")
                    .Add($"rest={_serfClient.ApiConfigurationOptions.Advertise}")
                    .Add("-tag")
                    .Add($"pubkey={pubKey.ByteToHex()}")
                    .Add("-tag")
                    .Add($"nodeversion={Assembly.GetExecutingAssembly().GetName().Version}"));

                await cmd.Observe().ForEachAsync(cmdEvent =>
                {
                    switch (cmdEvent)
                    {
                        case StartedCommandEvent started:
                            _logger.Here().Information("Process started; ID: {@ID}", started.ProcessId);
                            _serfClient.ProcessId = started.ProcessId;
                            break;
                        case StandardOutputCommandEvent stdOut:
                            if (stdOut.Text.Contains("agent: Serf agent starting"))
                            {
                                _logger.Here().Information("Serf has started!");
                                _serfClient.ProcessStarted = true;
                            }
                            _logger.Here().Debug("Out> {@StdOut}", stdOut.Text);
                            break;
                        case StandardErrorCommandEvent stdErr:
                            _logger.Here().Error("Err> {@StdErr}", stdErr.Text);
                            _serfClient.ProcessError = stdErr.Text;
                            break;
                        case ExitedCommandEvent exited:
                            _logger.Here().Information("Process exited; Code: {@ExitCode}", exited.ExitCode);
                            applicationLifetime.StopApplication();
                            break;
                    }
                }, applicationLifetime.ApplicationStopping).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Here().Error(ex, "Unable to initialize Serf");
                applicationLifetime.StopApplication();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static bool IsRunning(string name = "serf")
        {
            return Process.GetProcessesByName(name).Length > 0;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private Task<bool> TryUseExisting()
        {
            var cancellationToken = new CancellationTokenSource();
            bool existing = false;

            try
            {
                Task.Run(async () => { existing = await TryReconnect(cancellationToken); },
                    cancellationToken.Token);

                while (true)
                {
                    if (cancellationToken.Token.IsCancellationRequested)
                        cancellationToken.Token.ThrowIfCancellationRequested();

                    Task.Delay(100, cancellationToken.Token).Wait(cancellationToken.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Here().Error(ex, "Error while (re)connecting to Serf");
            }

            return Task.FromResult(existing);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<bool> TryReconnect(CancellationTokenSource cancellationToken)
        {
            bool connect = false;

            try
            {
                if (IsRunning())
                {
                    var tcpSession = _serfClient.GetTcpSession(_tcpSession.SessionId);
                    var connectResult = await _serfClient.Connect(tcpSession.SessionId);

                    if (!connectResult.Success)
                    {
                        cancellationToken.Cancel();
                        return connect;
                    }

                    var membersResult = await _serfClient.Members(tcpSession.SessionId);
                    if (!membersResult.Success)
                    {
                        cancellationToken.Cancel();
                        return connect;
                    }

                    var pubkey = await _signing.GetPublicKey(_signing.DefaultSigningKeyName);
                    if (pubkey == null)
                    {
                        cancellationToken.Cancel();
                    }
                    else
                    {
                        try
                        {
                            if (null != membersResult.Value.Members
                                .Where(member =>
                                    _serfClient.Name != member.Name && member.Status == "alive" && member.Tags.Count != 0)
                                .FirstOrDefault(x => x.Tags["pubkey"] == pubkey.ByteToHex()))
                            {
                                connect = true;
                            }
                        }
                        catch (KeyNotFoundException keyNotFoundException)
                        {
                            _logger.Here().Error(keyNotFoundException, "Public key was not found in member list");
                        }
                    }
                }
                else
                {
                    cancellationToken.Cancel();
                }
            }
            catch (OperationCanceledException)
            {

            }

            return connect;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="seedNode"></param>
        public async Task<bool> JoinSeedNodes(SeedNode seedNode)
        {
            try
            {
                var tcpSession = _serfClient.GetTcpSession(_tcpSession.SessionId);
                if (!tcpSession.Ready)
                {
                    tcpSession = tcpSession.Connect(_serfClient.SerfConfigurationOptions.RPC);
                    await _serfClient.Connect(tcpSession.SessionId);
                }

                var joinResult = await _serfClient.Join(seedNode.Seeds, tcpSession.SessionId);
                if (!joinResult.Success)
                {
                    _logger.Here().Error(((SerfError)joinResult.NonSuccessMessage).Error);
                    return false;
                }

                JoinedSeedNodes = true;
                _serfClient.SeedNodes.Seeds.AddRange(seedNode.Seeds);

                _logger.Here().Information("Serf might still be trying to join the seed nodes. Number of nodes joined: {@NumPeers}", joinResult.Value.Peers.ToString());
                return true;
            }
            catch (Exception ex)
            {
                _logger.Here().Fatal(ex, $"Unable to create Serf RPC address");
                return false;
            }
        }

        public bool JoinedSeedNodes { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private string GetFilePath()
        {
            var entryAssemblyPath = Helper.Util.EntryAssemblyPath();
            var platform = Helper.Util.GetOperatingSystemPlatform();
            string folder = platform.ToString().ToLowerInvariant();

            return Path.Combine(entryAssemblyPath, $"Serf/Terminal/{folder}/serf"); ;
        }
    }
}