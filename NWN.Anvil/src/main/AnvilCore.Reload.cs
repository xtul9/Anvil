using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Anvil.API;
using Anvil.API.Events;
using Anvil.Internal;
using Anvil.Plugins;
using Anvil.Services;
using LightInject;
using NWN.Core;

namespace Anvil
{
  public sealed partial class AnvilCore
  {
    private PluginFolderWatch? reloadRecovery;

    /// <summary>
    /// Initiates a complete reload of plugins and Anvil services.<br/>
    /// This will reload all plugins.
    /// </summary>
    public static void Reload()
    {
      Reload(NwObject.Invalid, null);
    }

    /// <summary>
    /// Initiates a complete reload of plugins and Anvil services.<br/>
    /// A reload that fails sends <paramref name="failureMessage"/> to <paramref name="requester"/>, since the plugin that
    /// asked is gone by then, and reloads again by itself once a new build lands in the plugin folders.
    /// </summary>
    /// <param name="requester">The creature to tell when the reload fails.</param>
    /// <param name="failureMessage">What to tell them, colour tokens included.</param>
    public static void Reload(uint requester, string? failureMessage)
    {
      ScheduleReload(new FailureAudience(requester, false, failureMessage));
    }

    /// <summary>
    /// Initiates a complete reload of plugins and Anvil services, for a request no one creature made.<br/>
    /// A reload that fails sends <paramref name="failureMessage"/> to every DM online, and reloads again by itself once a
    /// new build lands in the plugin folders.
    /// </summary>
    /// <param name="failureMessage">What to tell them, colour tokens included.</param>
    public static void ReloadTellingEveryDm(string failureMessage)
    {
      ScheduleReload(new FailureAudience(NwObject.Invalid, true, failureMessage));
    }

    private static void ScheduleReload(FailureAudience audience)
    {
      if (!EnvironmentConfig.ReloadEnabled)
      {
        Log.Error("Hot Reload of plugins is not enabled (ANVIL_RELOAD_ENABLED=true)");
        return;
      }

      GetService<SchedulerService>()?.Schedule(() => instance.RunReload(audience), TimeSpan.FromSeconds(0.1));
    }

    private void RunReload(FailureAudience audience)
    {
      Log.Info("Reloading Anvil");
      string? failure = TryRun(Unload);
      if (failure != null)
      {
        FailReload(failure, audience, PluginFolderWatch.Fingerprint(PluginRoots()));
        return;
      }

      LoadReloaded(audience);
    }

    private void LoadReloaded(FailureAudience audience)
    {
      reloadRecovery = null;
      long loaded = PluginFolderWatch.Fingerprint(PluginRoots());
      string? failure = TryRun(() =>
      {
        LoadAndStart();
        RaiseModuleLoadForReloadedServices();
      });

      if (failure != null)
      {
        FailReload(failure, audience, loaded);
      }
    }

    /// <summary>
    /// The failure as text: an exception thrown in plugin code holds that code through its stack trace, and would keep
    /// the plugins it should help unload in memory.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? TryRun(System.Action step)
    {
      try
      {
        step();
        return null;
      }
      catch (Exception e)
      {
        return e.ToString();
      }
    }

    /// <summary>
    /// Whatever the failed load built is unloaded at once, so no half-built set of plugins runs or waits to be unloaded
    /// later. The server goes on with no plugins until a working build lands in the plugin folders.
    /// </summary>
    private void FailReload(string failure, FailureAudience audience, long loaded)
    {
      Log.Error("Reload failed. Publish a working build: Anvil loads it once the plugin folders are quiet\n{Failure}", failure);
      string? unloadFailure = TryRun(Unload);
      if (unloadFailure != null)
      {
        Log.Error("Unloading what the failed reload built failed too\n{Failure}", unloadFailure);
      }

      Tell(audience);
      reloadRecovery = new PluginFolderWatch(PluginRoots(), loaded, () => LoadReloaded(audience));
    }

    /// <summary>
    /// The module loaded long before a reload, so services built by it subscribed to <see cref="ModuleEvents.OnModuleLoad"/>
    /// after the event fired. They hear it once more here, in the module's script context, without the module's own script.
    /// </summary>
    private void RaiseModuleLoadForReloadedServices()
    {
      EventService eventService = serviceManager.AnvilServiceContainer.GetInstance<EventService>();
      VirtualMachine virtualMachine = serviceManager.CoreServiceContainer.GetInstance<VirtualMachine>();

      Log.Info("Raising OnModuleLoad for the reloaded services");
      virtualMachine.ExecuteInScriptContext(() =>
      {
        eventService.ProcessEvent(EventCallbackType.Before, new ModuleEvents.OnModuleLoad());
        eventService.ProcessEvent(EventCallbackType.After, new ModuleEvents.OnModuleLoad());
      }, NwModule.Instance);
    }

    private void Tell(FailureAudience audience)
    {
      if (audience.FailureMessage is not { } message || (!audience.EveryDm && audience.Requester == NwObject.Invalid))
      {
        return;
      }

      VirtualMachine virtualMachine = serviceManager.CoreServiceContainer.GetInstance<VirtualMachine>();
      virtualMachine.ExecuteInScriptContext(() =>
      {
        if (audience.EveryDm)
        {
          NWScript.SendMessageToAllDMs(message);
        }
        else
        {
          NWScript.SendMessageToPC(audience.Requester, message);
        }
      });
    }

    private readonly record struct FailureAudience(uint Requester, bool EveryDm, string? FailureMessage);

    private static IReadOnlyList<string> PluginRoots()
    {
      return EnvironmentConfig.AdditionalPluginPaths.Prepend(HomeStorage.Plugins).Where(Directory.Exists).ToList();
    }
  }
}
