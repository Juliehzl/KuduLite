﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Kudu.Console.Services;
using Kudu.Contracts.Infrastructure;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Core.Deployment;
using Kudu.Core.Deployment.Generator;
using Kudu.Core.Helpers;
using Kudu.Core.Hooks;
using Kudu.Core.Infrastructure;
using Kudu.Core.Settings;
using Kudu.Core.SourceControl;
using Kudu.Core.SourceControl.Git;
using Kudu.Core.Tracing;
using System.Reflection;
using XmlSettings;
using k8s;
using IRepository = Kudu.Core.SourceControl.IRepository;
using log4net;
using log4net.Config;
using k8s.Models;
using System.Linq;
using System.Text;
using Kudu.Core.K8SE;

namespace Kudu.Console
{
    internal class Program
    {
        private static IEnvironment env;
        private static IDeploymentSettingsManager settingsManager;
        private static string appRoot;

        private static int Main(string[] args)
        {
            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            XmlConfigurator.Configure(logRepository, new FileInfo("log4net.config"));
            // Turn flag on in app.config to wait for debugger on launch
            if (ConfigurationManager.AppSettings["WaitForDebuggerOnStart"] == "true")
            {
                while (!Debugger.IsAttached)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }

            if (System.Environment.GetEnvironmentVariable(SettingsKeys.DisableDeploymentOnPush) == "1")
            {
                return 0;
            }

            if (args.Length < 2)
            {
                System.Console.WriteLine("Usage: kudu.exe appRoot wapTargets [deployer]");
                return 1;
            }

            // The post receive hook launches the exe from sh and intereprets newline differently.
            // This fixes very wacky issues with how the output shows up in the console on push
            System.Console.Error.NewLine = "\n";
            System.Console.Out.NewLine = "\n";

            appRoot = args[0];
            string wapTargets = args[1];
            string deployer = args.Length == 2 ? null : args[2];
            string requestId = System.Environment.GetEnvironmentVariable(Constants.RequestIdHeader);
            var dict = System.Environment.GetEnvironmentVariables();
            foreach (var envkey in dict.Keys)
            {
                System.Console.WriteLine(envkey.ToString() + ":" + dict[envkey].ToString());
            }
            System.Console.WriteLine("========================================================================");

            env = GetEnvironment(appRoot, requestId);
            ISettings settings = new XmlSettings.Settings(GetSettingsPath(env));
            settingsManager = new DeploymentSettingsManager(settings);

            // Setup the trace
            TraceLevel level = settingsManager.GetTraceLevel();
            ITracer tracer = GetTracer(env, level);
            ITraceFactory traceFactory = new TracerFactory(() => tracer);

            // Calculate the lock path
            string lockPath = Path.Combine(env.SiteRootPath, Constants.LockPath);
            string deploymentLockPath = Path.Combine(lockPath, Constants.DeploymentLockFile);

            IOperationLock deploymentLock = DeploymentLockFile.GetInstance(deploymentLockPath, traceFactory);

            dict = System.Environment.GetEnvironmentVariables();
            var process = Process.GetCurrentProcess();
            System.Console.WriteLine(process.ProcessName);
            foreach (var envkey in dict.Keys)
            {
                System.Console.WriteLine(envkey.ToString() + ":" +dict[envkey].ToString());
            }

            return PerformDeploy2(appRoot, wapTargets, deployer, lockPath, env, settingsManager, level, tracer, traceFactory, deploymentLock);
        }

        private static int oldMain(string[] args)
        {
            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            XmlConfigurator.Configure(logRepository, new FileInfo("log4net.config"));
            // Turn flag on in app.config to wait for debugger on launch
            if (ConfigurationManager.AppSettings["WaitForDebuggerOnStart"] == "true")
            {
                while (!Debugger.IsAttached)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }

            if (System.Environment.GetEnvironmentVariable(SettingsKeys.DisableDeploymentOnPush) == "1")
            {
                return 0;
            }

            if (args.Length < 2)
            {
                System.Console.WriteLine("Usage: kudu.exe appRoot wapTargets [deployer]");
                return 1;
            }

            // The post receive hook launches the exe from sh and intereprets newline differently.
            // This fixes very wacky issues with how the output shows up in the console on push
            System.Console.Error.NewLine = "\n";
            System.Console.Out.NewLine = "\n";

            appRoot = args[0];
            string wapTargets = args[1];
            string deployer = args.Length == 2 ? null : args[2];
            string requestId = System.Environment.GetEnvironmentVariable(Constants.RequestIdHeader);

            env = GetEnvironment(appRoot, requestId);
            ISettings settings = new XmlSettings.Settings(GetSettingsPath(env));
            settingsManager = new DeploymentSettingsManager(settings);

            // Setup the trace
            TraceLevel level = settingsManager.GetTraceLevel();
            ITracer tracer = GetTracer(env, level);
            ITraceFactory traceFactory = new TracerFactory(() => tracer);

            // Calculate the lock path
            string lockPath = Path.Combine(env.SiteRootPath, Constants.LockPath);
            string deploymentLockPath = Path.Combine(lockPath, Constants.DeploymentLockFile);

            IOperationLock deploymentLock = DeploymentLockFile.GetInstance(deploymentLockPath, traceFactory);

            if (deploymentLock.IsHeld)
            {
                return PerformDeploy(appRoot, wapTargets, deployer, lockPath, env, settingsManager, level, tracer, traceFactory, deploymentLock);
            }

            // Cross child process lock is not working on linux via mono.
            // When we reach here, deployment lock must be HELD! To solve above issue, we lock again before continue.
            try
            {
                return deploymentLock.LockOperation(() =>
                {
                    return PerformDeploy(appRoot, wapTargets, deployer, lockPath, env, settingsManager, level, tracer, traceFactory, deploymentLock);
                }, "Performing deployment", TimeSpan.Zero);
            }
            catch (LockOperationException)
            {
                return -1;
            }
        }

        private static int PerformDeploy2(
            string appRoot,
            string wapTargets,
            string deployer,
            string lockPath,
            IEnvironment env,
            IDeploymentSettingsManager settingsManager,
            TraceLevel level,
            ITracer tracer,
            ITraceFactory traceFactory,
            IOperationLock deploymentLock)
        {
            System.Environment.SetEnvironmentVariable("GIT_DIR", null, System.EnvironmentVariableTarget.Process);

            // Skip SSL Certificate Validate
            if (System.Environment.GetEnvironmentVariable(SettingsKeys.SkipSslValidation) == "1")
            {
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            }

            // Adjust repo path
            env.RepositoryPath = Path.Combine(env.SiteRootPath, settingsManager.GetRepositoryPath());
            string statusLockPath = Path.Combine(lockPath, Constants.StatusLockFile);
            string hooksLockPath = Path.Combine(lockPath, Constants.HooksLockFile);

            IOperationLock statusLock = new LockFile(statusLockPath, traceFactory);

            IBuildPropertyProvider buildPropertyProvider = new BuildPropertyProvider();

            ISiteBuilderFactory builderFactory = new SiteBuilderFactory(buildPropertyProvider, env, null);
            var logger = new ConsoleLogger(); IOperationLock hooksLock = new LockFile(hooksLockPath, traceFactory);

            IRepository gitRepository;
            if (settingsManager.UseLibGit2SharpRepository())
            {
                gitRepository = new LibGit2SharpRepository(env, settingsManager, traceFactory);
                System.Console.WriteLine("LibGit2");
            }
            else
            {
                gitRepository = new GitExeRepository(env, settingsManager, traceFactory);
                System.Console.WriteLine("Git");
            }

            env.CurrId = gitRepository.GetChangeSet(settingsManager.GetBranch()).Id;

            IServerConfiguration serverConfiguration = new ServerConfiguration();

            IAnalytics analytics = new Analytics(settingsManager, serverConfiguration, traceFactory);

            IWebHooksManager hooksManager = new WebHooksManager(tracer, env, hooksLock);

            IDeploymentStatusManager deploymentStatusManager = new DeploymentStatusManager(env, analytics, statusLock);
            //PackageArtifactFromFolder(env, settingsManager, tracer, GetLogger(env, level, logger), "artificial");

            var step = tracer.Step(XmlTracer.ExecutingExternalProcessTrace, new Dictionary<string, string>
            {
                { "type", "process" },
                { "path", "kudu.exe" },
                { "arguments", appRoot + " " + wapTargets }
            });

            using (step)
            {
                try
                {

                    var config = KubernetesClientConfiguration.BuildDefaultConfig();
                    IKubernetes client = new Kubernetes(config);
                    tracer.Trace("Starting Request!");

                    var secrets = client.ListNamespacedSecret("appservice-ns").Items;
                    var appSettings = secrets.FirstOrDefault(s => s.Metadata.Name == env.K8SEAppName);
                    string gitUri = null;
                    string customConfigMapName = K8SEDeploymentHelper.GetCustomConfigMap(env.K8SEAppName);
                    tracer.Trace(customConfigMapName);
                    var parts = customConfigMapName.Split("/");
                    tracer.Trace(parts.Length.ToString());
                    tracer.Trace(parts[0]);
                    tracer.Trace(parts[1]);
                    var customConfigMaps = client.ListNamespacedConfigMap(parts[0]).Items;
                    foreach (var m in customConfigMaps)
                    {
                        tracer.Trace(m.Metadata.Name);
                        tracer.Trace((m.Metadata.Name == parts[1].Trim()).ToString());
                    }
                    //var customConfigMap = customConfigMaps.FirstOrDefault(c => c.Metadata.Name == parts[1].Trim());
                    var customConfigMap = customConfigMaps.FirstOrDefault(c => c.Metadata.Name == parts[1].Trim());
                    if (appSettings != null && appSettings.Data.ContainsKey("password") && appSettings.Data.ContainsKey("user") && customConfigMap.Data.ContainsKey("DEFAULT_DNS_SUFFIX"))
                    {
                        var dnsName = customConfigMap.Data["DEFAULT_DNS_SUFFIX"];
                        //gitUri = $"https://%24layliunode14:19698240-cf9c-4794-8b82-f2538f158d2a@layliunode14.scm.layliueuapkube-7jpod2sie.centraluseuap.k4apps.io/layliunode14.git"
                        gitUri = string.Format("https://{0}:{1}@{2}/{3}.git", Encoding.UTF8.GetString(appSettings.Data["user"]).Replace("$", "%24"), Encoding.UTF8.GetString(appSettings.Data["password"]),
                            $"{env.K8SEAppName}.scm.{dnsName}", env.K8SEAppName);

                        tracer.Trace(dnsName);
                        tracer.Trace(gitUri);
                    }
                    else
                    {
                        //TODO: Need to fallback to the existing kudu service which has only one build service support.
                        throw new Exception("Throw exception or handle some existing cases");
                    }

                    string buildJobPodName = Guid.NewGuid().ToString()[..4];
                    string podDeploymentName = System.Environment.GetEnvironmentVariable(Constants.PodDeploymentName);

                    var pod = client.CreateNamespacedPod(
                        new V1Pod()
                        {
                            Metadata = new V1ObjectMeta { Name = "build-job-" + buildJobPodName },
                            Spec = new V1PodSpec
                            {
                                RestartPolicy = "Never",
                                Containers = new[]
                                {
                                    new V1Container()
                                    {
                                        Name = "container",
                                        Image = "layliuregistry.azurecr.io/build-job:v6",
                                        Command = new List<string>() { "/bin/sh", "-c" },
                                        Args = new List<string>(){ $"cd /opt/Kudu; mkdir -p {appRoot}; dotnet ./KuduBuildJob/Kudu.BuildJob.dll {appRoot} {gitUri} 2> {appRoot}/err.log; sleep 1200s" },
                                        Env = new List<V1EnvVar>
                                        {
                                            new V1EnvVar
                                            {
                                                Name = "SYSTEM_NAMESPACE",
                                                ValueFrom = new V1EnvVarSource{  FieldRef = new V1ObjectFieldSelector{  ApiVersion = "v1", FieldPath = "metadata.namespace"} }
                                            },
                                            new V1EnvVar
                                            {
                                                Name = "POD_NAME",
                                                ValueFrom = new V1EnvVarSource{  FieldRef = new V1ObjectFieldSelector{  ApiVersion = "v1", FieldPath = "metadata.name"} }
                                            },
                                            new V1EnvVar
                                            {
                                                Name = "POD_NAMESPACE",
                                                ValueFrom = new V1EnvVarSource{  FieldRef = new V1ObjectFieldSelector{  ApiVersion = "v1", FieldPath = "metadata.namespace"} }
                                            },
                                            new V1EnvVar
                                            {
                                                Name = "POD_DEPLOYMENT_NAME",
                                                Value = "layliueuapkube-k8se-build-job"
                                            },
                                            new V1EnvVar
                                            {
                                                Name = "IS_BUILD_JOB",
                                                Value = "true"
                                            }
                                        },
                                        EnvFrom = new List<V1EnvFromSource>
                                        {
                                            new V1EnvFromSource(){ ConfigMapRef = new V1ConfigMapEnvSource(){ Name = podDeploymentName} },
                                            new V1EnvFromSource(){ SecretRef = new V1SecretEnvSource(){ Name = env.K8SEAppName} },
                                            new V1EnvFromSource(){ ConfigMapRef = new V1ConfigMapEnvSource(){ Name = customConfigMap.Metadata.Name} }
                                        },
                                        ImagePullPolicy = "Always"
                                    },
                                },
                                ServiceAccountName = podDeploymentName
                            },
                        },
                    "appservice-ns");

                    //var pod = Yaml.LoadFromString<V1Pod>(podYaml);
                }
                catch (Exception e)
                {
                    tracer.TraceError(e);
                    return 1;
                }
            }

            tracer.Step("Perform deploy exiting successfully");
            return 0;
        }

        private static string PackageArtifactFromFolder(IEnvironment environment, IDeploymentSettingsManager settings, ITracer tracer
            , ILogger logger, string artifactFilename)
        {
            tracer.Trace("Writing the artifacts to a squashfs file");
            string file = Path.Combine(environment.DeploymentsPath, artifactFilename);
            ExternalCommandFactory commandFactory = new ExternalCommandFactory(environment, settings, env.RepositoryPath);
            Executable exe = commandFactory.BuildExternalCommandExecutable(environment.RepositoryPath, environment.DeploymentsPath, logger);
            try
            {
                exe.ExecuteWithProgressWriter(logger, tracer, $"mksquashfs . {file} -noappend");
            }
            catch (Exception)
            {
                logger.LogError();
                throw;
            }

            return file;
        }

        private static int PerformDeploy(
            string appRoot,
            string wapTargets,
            string deployer,
            string lockPath,
            IEnvironment env,
            IDeploymentSettingsManager settingsManager,
            TraceLevel level,
            ITracer tracer,
            ITraceFactory traceFactory,
            IOperationLock deploymentLock)
        {
            System.Environment.SetEnvironmentVariable("GIT_DIR", null, System.EnvironmentVariableTarget.Process);

            // Skip SSL Certificate Validate
            if (System.Environment.GetEnvironmentVariable(SettingsKeys.SkipSslValidation) == "1")
            {
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            }

            // Adjust repo path
            env.RepositoryPath = Path.Combine(env.SiteRootPath, settingsManager.GetRepositoryPath());
            string statusLockPath = Path.Combine(lockPath, Constants.StatusLockFile);
            string hooksLockPath = Path.Combine(lockPath, Constants.HooksLockFile);


            IOperationLock statusLock = new LockFile(statusLockPath, traceFactory);
            IOperationLock hooksLock = new LockFile(hooksLockPath, traceFactory);

            IBuildPropertyProvider buildPropertyProvider = new BuildPropertyProvider();
            ISiteBuilderFactory builderFactory = new SiteBuilderFactory(buildPropertyProvider, env, null);
            var logger = new ConsoleLogger();

            IRepository gitRepository;
            if (settingsManager.UseLibGit2SharpRepository())
            {
                gitRepository = new LibGit2SharpRepository(env, settingsManager, traceFactory);
            }
            else
            {
                gitRepository = new GitExeRepository(env, settingsManager, traceFactory);
            }

            env.CurrId = gitRepository.GetChangeSet(settingsManager.GetBranch()).Id;

            IServerConfiguration serverConfiguration = new ServerConfiguration();

            IAnalytics analytics = new Analytics(settingsManager, serverConfiguration, traceFactory);

            IWebHooksManager hooksManager = new WebHooksManager(tracer, env, hooksLock);

            IDeploymentStatusManager deploymentStatusManager = new DeploymentStatusManager(env, analytics, statusLock);

            IDeploymentManager deploymentManager = new DeploymentManager(builderFactory,
                                                          env,
                                                          traceFactory,
                                                          analytics,
                                                          settingsManager,
                                                          deploymentStatusManager,
                                                          deploymentLock,
                                                          GetLogger(env, level, logger),
                                                          hooksManager,
                                                          null); // K8 todo

            var step = tracer.Step(XmlTracer.ExecutingExternalProcessTrace, new Dictionary<string, string>
            {
                { "type", "process" },
                { "path", "kudu.exe" },
                { "arguments", appRoot + " " + wapTargets }
            });

            using (step)
            {
                try
                {
                    // although the api is called DeployAsync, most expensive works are done synchronously.
                    // need to launch separate task to go async explicitly (consistent with FetchDeploymentManager)
                    var deploymentTask = Task.Run(async () => await deploymentManager.DeployAsync(gitRepository, changeSet: null, deployer: deployer, clean: false));

#pragma warning disable 4014
                    // Track pending task
                    PostDeploymentHelper.TrackPendingOperation(deploymentTask, TimeSpan.Zero);
#pragma warning restore 4014

                    deploymentTask.Wait();

                    if (PostDeploymentHelper.IsAutoSwapEnabled())
                    {
                        string branch = settingsManager.GetBranch();
                        ChangeSet changeSet = gitRepository.GetChangeSet(branch);
                        IDeploymentStatusFile statusFile = deploymentStatusManager.Open(changeSet.Id, env);
                        if (statusFile != null && statusFile.Status == DeployStatus.Success)
                        {
                            PostDeploymentHelper.PerformAutoSwap(env.RequestId,
                                    new PostDeploymentTraceListener(tracer, deploymentManager.GetLogger(changeSet.Id)))
                                .Wait();
                        }
                    }
                }
                catch (Exception e)
                {
                    System.Console.WriteLine(e.InnerException);
                    tracer.TraceError(e);
                    System.Console.Error.WriteLine(e.GetBaseException().Message);
                    System.Console.Error.WriteLine(Resources.Log_DeploymentError);
                    return 1;
                }
                finally
                {
                    System.Console.WriteLine("Deployment Logs : '" +
                    env.AppBaseUrlPrefix + "/newui/jsonviewer?view_url=/api/deployments/" +
                    gitRepository.GetChangeSet(settingsManager.GetBranch()).Id + "/log'");
                }
            }

            if (logger.HasErrors)
            {
                return 1;
            }
            tracer.Step("Perform deploy exiting successfully");
            return 0;
        }

        private static ITracer GetTracer(IEnvironment env, TraceLevel level)
        {
            if (level > TraceLevel.Off)
            {
                var tracer = new XmlTracer(env.TracePath, level);
                string logFile = System.Environment.GetEnvironmentVariable(Constants.TraceFileEnvKey);
                if (!String.IsNullOrEmpty(logFile))
                {
                    // Kudu.exe is executed as part of git.exe (post-receive), giving its initial depth of 4 indentations
                    string logPath = Path.Combine(env.TracePath, logFile);
                    // since git push is "POST", which then run kudu.exe
                    return new CascadeTracer(tracer, new TextTracer(logPath, level, 4), new ETWTracer(env.RequestId, requestMethod: HttpMethod.Post.Method));
                }

                return tracer;
            }

            return NullTracer.Instance;
        }

        private static ILogger GetLogger(IEnvironment env, TraceLevel level, ILogger primary)
        {
            if (level > TraceLevel.Off)
            {
                string logFile = System.Environment.GetEnvironmentVariable(Constants.TraceFileEnvKey);
                if (!String.IsNullOrEmpty(logFile))
                {
                    string logPath = Path.Combine(env.RootPath, Constants.DeploymentTracePath, logFile);
                    //return new CascadeLogger(primary, new TextLogger(logPath));
                    return new CascadeLogger(primary, new TextLogger(logPath));
                }
            }

            return primary;
        }

        private static string GetSettingsPath(IEnvironment environment)
        {
            return Path.Combine(environment.DeploymentsPath, Constants.DeploySettingsPath);
        }

        private static IEnvironment GetEnvironment(string siteRoot, string requestId)
        {
            string root = Path.GetFullPath(Path.Combine(siteRoot, ".."));
            string appName = root.Replace("/home/apps/","");

            // CORE TODO : test by setting SCM_REPOSITORY_PATH 
            // REVIEW: this looks wrong because it ignores SCM_REPOSITORY_PATH
            string repositoryPath = Path.Combine(siteRoot, Constants.RepositoryPath);

            // SCM_BIN_PATH is introduced in Kudu apache config file 
            // Provide a way to override Kudu bin path, to resolve issue where we can not find the right Kudu bin path when running on mono
            // CORE TODO I don't think this is needed anymore? This env var is not used anywhere but here.
            string binPath = System.Environment.GetEnvironmentVariable("SCM_BIN_PATH");
            if (string.IsNullOrWhiteSpace(binPath))
            {
                // CORE TODO Double check. Process.GetCurrentProcess() always gets the dotnet.exe process,
                // so changed to Assembly.GetEntryAssembly().Location
                binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            }
            System.Console.WriteLine($"BUILDJOB====== binPath {binPath}");
            // CORE TODO Handing in a null IHttpContextAccessor (and KuduConsoleFullPath) again
            var env=  new Kudu.Core.Environment(root,
                EnvironmentHelper.NormalizeBinPath(binPath),
                repositoryPath,
                requestId,
                Path.Combine(AppContext.BaseDirectory, "KuduConsole", "kudu.dll"),
                null,
                appName);
            System.Console.WriteLine($"BUILDJOB====== scriptPath {env.ScriptPath}");
            return env;
        }
    }
}