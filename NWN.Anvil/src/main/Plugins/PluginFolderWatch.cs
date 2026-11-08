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

    private long seen;
    private DateTime nextCheck = DateTime.MinValue;
    private DateTime? changedAt;

    /// <param name="roots">The plugin folders.</param>
    /// <param name="loaded">The <see cref="Fingerprint"/> of the build that was loaded: a build published while it failed counts as new.</param>
    /// <param name="onQuietChange">What to run once the folders differ from it and stay quiet.</param>
    public PluginFolderWatch(IReadOnlyList<string> roots, long loaded, Action onQuietChange)
    {
      this.roots = roots;
      this.onQuietChange = onQuietChange;
      seen = loaded;
      changedAt = Fingerprint(roots) != loaded ? DateTime.UtcNow : null;
    }

    public void Poll()
    {
      DateTime now = DateTime.UtcNow;
      if (now < nextCheck)
      {
        return;
      }

      nextCheck = now + CheckInterval;
      long current = Fingerprint(roots);
      if (current != seen)
      {
        seen = current;
        changedAt = now;
        return;
      }

      if (changedAt is { } changed && now - changed >= QuietTime)
      {
        changedAt = null;
        onQuietChange();
      }
    }

    /// <summary>Every file's path, size and write time: a build moved in keeps the times it was built with, which may be older.</summary>
    public static long Fingerprint(IReadOnlyList<string> roots)
    {
      HashCode hash = new HashCode();
      foreach (FileInfo file in roots.SelectMany(root => new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)).OrderBy(file => file.FullName, StringComparer.Ordinal))
      {
        hash.Add(file.FullName);
        hash.Add(file.Length);
        hash.Add(file.LastWriteTimeUtc);
      }

      return hash.ToHashCode();
    }
  }
}
