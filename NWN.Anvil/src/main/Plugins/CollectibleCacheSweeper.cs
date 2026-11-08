using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using NLog;

namespace Anvil.Plugins
{
  /// <summary>
  /// Drops what long-lived code outside the plugins remembers of plugins being unloaded, so their load contexts can be
  /// collected. The libraries Anvil shares with every plugin cache by type, the runtime lists every meter for the life of
  /// the process, and Anvil's core container caches what it resolved; any of them keeps an unloaded plugin in memory.
  /// </summary>
  internal sealed class CollectibleCacheSweeper
  {
    private const int MaxSweepDepth = 10;
    private const int MaxHoldDepth = 6;

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly string[] SweptLibraries = ["LightInject", "Newtonsoft.Json", "System.Text.Json", "System.ComponentModel.TypeConverter"];

    /// <summary>Libraries whose static collections are swept, but whose objects are not walked into.</summary>
    private static readonly string[] SweptStatics = ["NWN.Anvil", "NWN.Core"];

    private static readonly Type[] SerializationAttributes =
    [
      typeof(System.Runtime.Serialization.DataContractAttribute),
      typeof(System.Runtime.Serialization.DataMemberAttribute),
      typeof(NonSerializedAttribute),
      typeof(SerializableAttribute),
    ];

    private readonly HashSet<AssemblyLoadContext> unloading;
    private readonly HashSet<object> swept = new HashSet<object>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> dropped = new Dictionary<string, int>();

    private void Note(string kind, object? what)
    {
      string key = $"{kind} {Describe(what)}";
      dropped[key] = dropped.GetValueOrDefault(key) + 1;
    }

    private static string Describe(object? what)
    {
      return what switch
      {
        Delegate callback => $"{callback.Method.DeclaringType?.FullName}.{callback.Method.Name}",
        Type type => type.FullName ?? type.Name,
        null => "null",
        _ => what.GetType().FullName ?? what.GetType().Name,
      };
    }

    private CollectibleCacheSweeper(IEnumerable<AssemblyLoadContext> unloading)
    {
      this.unloading = unloading.ToHashSet();
    }

    public static void Sweep(object longLivedContainer, IEnumerable<AssemblyLoadContext> unloading, bool firstPass)
    {
      try
      {
        SweepAll(longLivedContainer, unloading, firstPass);
      }
      catch (Exception e)
      {
        Log.Error(e, "Sweeping cached references to unloaded plugins failed; they may stay in memory");
      }
    }

    private static void SweepAll(object longLivedContainer, IEnumerable<AssemblyLoadContext> unloading, bool firstPass)
    {
      CollectibleCacheSweeper sweeper = new CollectibleCacheSweeper(unloading);
      if (sweeper.unloading.Count == 0)
      {
        return;
      }

      // Once a collection has run, an unloading plugin's allocator may be half torn down: only the first pass reads the
      // plugins themselves. Later passes look only at long-lived objects, and whatever those still reference is alive.
      if (firstPass)
      {
        RunMetadataUpdateHandlers();
      }

      int removed = sweeper.SweepObject(longLivedContainer, 0);
      foreach (Assembly library in LongLivedAssemblies().Where(assembly => IsSweptLibrary(assembly) || SweptStatics.Contains(assembly.GetName().Name)))
      {
        foreach (Type type in StaticCacheHolders(library))
        {
          removed += sweeper.SweepStatics(type);
        }
      }

      removed += sweeper.DisposeMeters();
      removed += sweeper.PruneProcessEvents();
      removed += firstPass ? sweeper.DisposeThreadLocals() : 0;
      removed += sweeper.DisposeTimers();

      Log.Info("Dropped {Count} cached references to the {Plugins} plugin(s) being unloaded: {Dropped}", removed, sweeper.unloading.Count,
        string.Join("; ", sweeper.dropped.OrderByDescending(entry => entry.Value).Select(entry => $"{entry.Value}x {entry.Key}")));
    }

    /// <summary>
    /// The swept libraries' own cache handlers, the ones hot reload uses. The runtime's type cache handler is left out:
    /// it walks every loaded type, and an allocator being torn down makes it fault.
    /// </summary>
    private static void RunMetadataUpdateHandlers()
    {
      foreach (Assembly assembly in LongLivedAssemblies().Where(IsSweptLibrary))
      {
        foreach (MetadataUpdateHandlerAttribute handler in assembly.GetCustomAttributes<MetadataUpdateHandlerAttribute>())
        {
          MethodInfo? clearCache = handler.HandlerType.GetMethod("ClearCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Type[])]);
          try
          {
            clearCache?.Invoke(null, [null]);
          }
          catch (Exception e)
          {
            Log.Warn(e, "Cache handler {Handler} failed", handler.HandlerType.FullName);
          }
        }
      }
    }

    /// <summary>NWNX hosts Anvil in a load context of its own, so Anvil's libraries are not all in the default one.</summary>
    private static IEnumerable<Assembly> LongLivedAssemblies()
    {
      return AssemblyLoadContext.All.Where(context => !context.IsCollectible).SelectMany(context => context.Assemblies).Distinct();
    }

    private static bool IsSweptLibrary(Assembly assembly)
    {
      return SweptLibraries.Contains(assembly.GetName().Name);
    }

    private static IEnumerable<Type> StaticCacheHolders(Assembly library)
    {
      Type[] types;
      try
      {
        types = library.GetTypes();
      }
      catch (ReflectionTypeLoadException e)
      {
        types = e.Types.OfType<Type>().ToArray();
      }

      // Newtonsoft keeps one attribute cache per attribute type, as a static of a generic class.
      Type[] attributes = types.Where(type => typeof(Attribute).IsAssignableFrom(type)).Concat(SerializationAttributes).ToArray();
      foreach (Type type in types)
      {
        if (!type.IsGenericTypeDefinition)
        {
          yield return type;
        }
        else if (IsSweptLibrary(library) && type.GetGenericArguments().Length == 1)
        {
          foreach (Type attribute in attributes)
          {
            Type? closed = TryClose(type, attribute);
            if (closed != null)
            {
              yield return closed;
            }
          }
        }
      }
    }

    private static Type? TryClose(Type genericDefinition, Type argument)
    {
      try
      {
        return genericDefinition.MakeGenericType(argument);
      }
      catch (ArgumentException)
      {
        return null;
      }
    }

    private static IEnumerable<FieldInfo> InstanceFields(Type type)
    {
      for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
      {
        foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
          yield return field;
        }
      }
    }

    private static bool IsReferenceField(FieldInfo field)
    {
      return !field.FieldType.IsPrimitive && !field.FieldType.IsEnum && !field.FieldType.IsPointer;
    }

    private static bool IsLongLivedPlumbing(Type type)
    {
      if (type.IsCollectible)
      {
        return false;
      }

      return SweptLibraries.Contains(type.Assembly.GetName().Name)
        || type.Namespace?.StartsWith("System.Collections") == true
        || type.Namespace?.StartsWith("System.Threading") == true
        || type.Namespace?.StartsWith("System.Runtime.CompilerServices") == true
        || type.Namespace == "System.Diagnostics.Metrics";
    }

    private static bool IsLightInjectTree(Type type)
    {
      return type.Assembly.GetName().Name == "LightInject" && type.Name.StartsWith("ImmutableHash");
    }

    private static object? EmptyOf(Type treeType)
    {
      return treeType.GetField("Empty", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
        ?? treeType.GetProperty("Empty", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
    }

    private int SweepStatics(Type type)
    {
      int removed = 0;
      foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
      {
        if (field.IsLiteral || !IsReferenceField(field))
        {
          continue;
        }

        object? value;
        try
        {
          value = field.GetValue(null);
        }
        catch (Exception)
        {
          continue;
        }

        removed += SweepObject(value, 0);
      }

      return removed;
    }

    private int SweepObject(object? value, int depth)
    {
      if (value == null || depth > MaxSweepDepth || value is Type or Assembly or string || value.GetType().IsPrimitive || !swept.Add(value))
      {
        return 0;
      }

      int removed = value is IDictionary dictionary ? RemoveUnloadingEntries(dictionary) : 0;
      if (value is Array array)
      {
        foreach (object? element in array)
        {
          removed += SweepObject(element, depth + 1);
        }

        return removed;
      }

      if (!IsLongLivedPlumbing(value.GetType()))
      {
        return removed;
      }

      foreach (FieldInfo field in InstanceFields(value.GetType()).Where(IsReferenceField))
      {
        object? child = field.GetValue(value);
        if (child != null && IsLightInjectTree(child.GetType()) && !field.IsInitOnly && Holds(child))
        {
          object? empty = EmptyOf(child.GetType());
          if (empty != null)
          {
            field.SetValue(value, empty);
            Note("tree", value);
            removed++;
            continue;
          }
        }

        removed += SweepObject(child, depth + 1);
      }

      return removed;
    }

    private int RemoveUnloadingEntries(IDictionary dictionary)
    {
      List<object> stale = [];
      try
      {
        foreach (DictionaryEntry entry in dictionary)
        {
          if (Holds(entry.Key) || Holds(entry.Value))
          {
            stale.Add(entry.Key);
          }
        }

        foreach (object key in stale)
        {
          Note("cache " + dictionary.GetType().Name, key);
          dictionary.Remove(key);
        }
      }
      catch (Exception e)
      {
        Log.Warn(e, "Could not sweep a {Dictionary}", dictionary.GetType().FullName);
        return 0;
      }

      return stale.Count;
    }

    /// <summary>
    /// Every meter is listed in the runtime for the life of the process. A library a plugin carries (Npgsql) makes
    /// its meter with callbacks into its own code, and never disposes it.
    /// </summary>
    private int DisposeMeters()
    {
      FieldInfo? allMetersField = typeof(Meter).GetField("s_allMeters", BindingFlags.Static | BindingFlags.NonPublic);
      if (allMetersField?.GetValue(null) is not IList allMeters)
      {
        return 0;
      }

      List<Meter> stale;
      lock (allMeters)
      {
        stale = allMeters.OfType<Meter>().Where(Holds).ToList();
      }

      foreach (Meter meter in stale)
      {
        Note("meter " + meter.Name, null);
        meter.Dispose();
      }

      return stale.Count;
    }

    /// <summary>
    /// <see cref="AppContext"/> and <see cref="AppDomain"/> events live as long as the process. A library a plugin carries
    /// (Npgsql's pool manager) subscribes from a static constructor and never unsubscribes.
    /// </summary>
    private int PruneProcessEvents()
    {
      int removed = 0;
      foreach (FieldInfo field in typeof(AppContext).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(field => typeof(Delegate).IsAssignableFrom(field.FieldType)))
      {
        removed += PruneDelegateField(field, null);
      }

      foreach (FieldInfo field in InstanceFields(typeof(AppDomain)).Where(field => typeof(Delegate).IsAssignableFrom(field.FieldType)))
      {
        removed += PruneDelegateField(field, AppDomain.CurrentDomain);
      }

      return removed;
    }

    private int PruneDelegateField(FieldInfo field, object? owner)
    {
      if (field.GetValue(owner) is not Delegate subscribers)
      {
        return 0;
      }

      Delegate[] stale = subscribers.GetInvocationList().Where(Holds).ToArray();
      Delegate? kept = subscribers;
      foreach (Delegate subscriber in stale)
      {
        Note("event " + field.Name, subscriber);
        kept = Delegate.Remove(kept, subscriber);
      }

      if (stale.Length > 0)
      {
        field.SetValue(owner, kept);
      }

      return stale.Length;
    }

    /// <summary>
    /// A <see cref="System.Threading.ThreadLocal{T}"/> of a framework type shares each thread's slot array with every
    /// other instance of that type, so one a plugin never disposed stays reachable from the next copy's, with its
    /// values (prometheus-net's collect callbacks live in one). Disposing it clears its slot on every thread.
    /// </summary>
    private int DisposeThreadLocals()
    {
      HashSet<object> visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
      List<IDisposable> threadLocals = [];
      foreach (Assembly assembly in unloading.SelectMany(context => context.Assemblies))
      {
        Type[] types;
        try
        {
          types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
          types = e.Types.OfType<Type>().ToArray();
        }

        foreach (Type type in types.Where(type => !type.IsGenericTypeDefinition))
        {
          foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
          {
            try
            {
              if (!field.IsLiteral && IsReferenceField(field))
              {
                CollectThreadLocals(field.GetValue(null), visited, threadLocals, 0);
              }
            }
            catch (Exception)
            {
              // The plugin's load context no longer resolves its dependencies, so a field typed by one cannot be read.
            }
          }
        }
      }

      foreach (IDisposable threadLocal in threadLocals)
      {
        Note("thread-local", threadLocal);
        threadLocal.Dispose();
      }

      return threadLocals.Count;
    }

    private void CollectThreadLocals(object? value, HashSet<object> visited, List<IDisposable> threadLocals, int depth)
    {
      if (value == null || depth > MaxSweepDepth || value is Type or Assembly or string || value.GetType().IsPrimitive || !visited.Add(value))
      {
        return;
      }

      Type type = value.GetType();
      if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Threading.ThreadLocal<>))
      {
        threadLocals.Add((IDisposable)value);
        return;
      }

      if (value is Array array)
      {
        if (!type.GetElementType()!.IsPrimitive)
        {
          foreach (object? element in array)
          {
            CollectThreadLocals(element, visited, threadLocals, depth + 1);
          }
        }

        return;
      }

      if (!IsUnloading(type) && type.Namespace?.StartsWith("System.Collections") != true)
      {
        return;
      }

      foreach (FieldInfo field in InstanceFields(type))
      {
        try
        {
          if (IsReferenceField(field))
          {
            CollectThreadLocals(field.GetValue(value), visited, threadLocals, depth + 1);
          }
        }
        catch (Exception)
        {
          // As above: an unresolvable field type.
        }
      }
    }

    /// <summary>
    /// The runtime's timer queues are static, so a timer or a <see cref="System.Threading.Tasks.Task.Delay(int)"/> whose
    /// continuation runs plugin code keeps the plugin until it fires, and a loop that awaits one keeps it forever.
    /// Disposing it means the plugin's code never resumes, which is right for a plugin that is gone.
    /// </summary>
    private int DisposeTimers()
    {
      Type? queueType = typeof(System.Threading.Timer).Assembly.GetType("System.Threading.TimerQueue");
      Type? timerType = typeof(System.Threading.Timer).Assembly.GetType("System.Threading.TimerQueueTimer");
      MethodInfo? close = timerType?.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
      if (queueType?.GetProperty("Instances", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) is not Array queues || close == null)
      {
        Log.Warn("The runtime's timer queues are not where this Anvil expects them; timers of unloaded plugins stay queued");
        return 0;
      }

      List<object> stale = [];
      foreach (object? queue in queues)
      {
        if (queue == null)
        {
          continue;
        }

        lock (queue)
        {
          foreach (string listName in new[] { "_shortTimers", "_longTimers" })
          {
            for (object? timer = queue.GetType().GetField(listName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(queue);
                 timer != null;
                 timer = timerType!.GetField("_next", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(timer))
            {
              if (Holds(timerType!.GetField("_timerCallback", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(timer))
                  || Holds(timerType.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(timer)))
              {
                stale.Add(timer);
              }
            }
          }
        }
      }

      foreach (object timer in stale)
      {
        Note("timer", timerType!.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(timer) ?? timerType.GetField("_timerCallback", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(timer));
        close.Invoke(timer, null);
      }

      return stale.Count;
    }

    private bool Holds(object? value)
    {
      return Holds(value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
    }

    private bool Holds(object? value, HashSet<object> visited, int depth)
    {
      switch (value)
      {
        case null:
          return false;
        case Type type:
          return IsUnloading(type);
        case Assembly assembly:
          return IsUnloading(assembly);
        case MemberInfo member:
          return IsUnloading(member.DeclaringType) || IsUnloading(member.ReflectedType);
        case string:
          return false;
      }

      Type valueType = value.GetType();
      if (IsUnloading(valueType))
      {
        return true;
      }

      if (depth > MaxHoldDepth || valueType.IsPrimitive || !visited.Add(value))
      {
        return false;
      }

      if (value is Delegate callback)
      {
        return IsUnloading(callback.Method.DeclaringType) || Holds(callback.Target, visited, depth + 1);
      }

      if (value is IEnumerable sequence and not IDictionary)
      {
        foreach (object? element in sequence)
        {
          if (Holds(element, visited, depth + 1))
          {
            return true;
          }
        }
      }

      if (!IsLongLivedPlumbing(valueType) && valueType.Namespace?.StartsWith("System.Reflection") != true)
      {
        return false;
      }

      return InstanceFields(valueType).Where(IsReferenceField).Any(field => Holds(field.GetValue(value), visited, depth + 1));
    }

    private bool IsUnloading(Assembly? assembly)
    {
      return assembly != null && assembly.IsCollectible && AssemblyLoadContext.GetLoadContext(assembly) is { } context && unloading.Contains(context);
    }

    private bool IsUnloading(Type? type)
    {
      if (type == null)
      {
        return false;
      }

      return IsUnloading(type.Assembly)
        || (type.IsGenericType && !type.IsGenericTypeDefinition && type.GetGenericArguments().Any(IsUnloading))
        || (type.HasElementType && IsUnloading(type.GetElementType()));
    }
  }
}
