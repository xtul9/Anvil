using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Anvil.Plugins
{
  /// <summary>
  /// Runs an action once the plugin folders change and then stay quiet, polled from the server loop. After a failed reload
  /// no plugin is left to notice a new build, so Anvil watches for it.
  /// </summary>
  internal sealed class PluginFolderWatch
  {
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<string> roots;
    private readonly Action onQuietChange;

    private DateTime newestSeen;
    private DateTime nextCheck = DateTime.MinValue;
    private DateTime? changedAt;

    public PluginFolderWatch(IReadOnlyList<string> roots, Action onQuietChange)
    {
      this.roots = roots;
      this.onQuietChange = onQuietChange;
      newestSeen = NewestWrite();
    }

    public void Poll()
    {
      DateTime now = DateTime.UtcNow;
      if (now < nextCheck)
      {
        return;
      }

      nextCheck = now + CheckInterval;
      DateTime newest = NewestWrite();
      if (newest > newestSeen)
      {
        newestSeen = newest;
        changedAt = now;
        return;
      }

      if (changedAt is { } changed && now - changed >= QuietTime)
      {
        changedAt = null;
        onQuietChange();
      }
    }

    private DateTime NewestWrite()
    {
      return roots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        .Select(File.GetLastWriteTimeUtc)
        .DefaultIfEmpty(DateTime.MinValue)
        .Max();
    }
  }
}
