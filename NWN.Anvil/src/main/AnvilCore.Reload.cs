using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
      if (!EnvironmentConfig.ReloadEnabled)
      {
        Log.Error("Hot Reload of plugins is not enabled (ANVIL_RELOAD_ENABLED=true)");
        return;
      }

      GetService<SchedulerService>()?.Schedule(() => instance.RunReload(requester, failureMessage), TimeSpan.FromSeconds(0.1));
    }

    private void RunReload(uint requester, string? failureMessage)
    {
      reloadRecovery = null;
      Log.Info("Reloading Anvil");
      try
      {
        Unload();
        LoadAndStart();
        RaiseModuleLoadForReloadedServices();
      }
      catch (Exception e)
      {
        Log.Error(e, "Reload failed. Publish a working build: Anvil reloads again once the plugin folders are quiet");
        TellRequester(requester, failureMessage);
        reloadRecovery = new PluginFolderWatch(PluginRoots(), () => RunReload(requester, failureMessage));
      }
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

    private void TellRequester(uint requester, string? message)
    {
      if (requester == NwObject.Invalid || message == null)
      {
        return;
      }

      VirtualMachine virtualMachine = serviceManager.CoreServiceContainer.GetInstance<VirtualMachine>();
      virtualMachine.ExecuteInScriptContext(() => NWScript.SendMessageToPC(requester, message));
    }

    private static IReadOnlyList<string> PluginRoots()
    {
      return EnvironmentConfig.AdditionalPluginPaths.Prepend(HomeStorage.Plugins).Where(Directory.Exists).ToList();
    }
  }
}
