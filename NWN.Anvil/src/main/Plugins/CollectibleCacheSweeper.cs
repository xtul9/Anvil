using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using NLog;

namespace Anvil.Plugins
{
  /// <summary>
  /// Drops what long-lived caches outside the plugins remember of unloaded plugin code, so a whole reload can collect it.
  /// The libraries Anvil shares with every plugin cache by type, and a cached plugin type keeps its plugin loaded.
  /// </summary>
  internal static class CollectibleCacheSweeper
  {
    private const int MaxDepth = 10;

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly string[] SweptLibraries = ["LightInject", "Newtonsoft.Json", "System.Text.Json", "System.ComponentModel.TypeConverter"];

    private static readonly Type[] SerializationAttributes =
    [
      typeof(System.Runtime.Serialization.DataContractAttribute),
      typeof(System.Runtime.Serialization.DataMemberAttribute),
      typeof(NonSerializedAttribute),
      typeof(SerializableAttribute),
    ];

    public static void Sweep(object longLivedContainer)
    {
      RunMetadataUpdateHandlers();

      HashSet<object> visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
      int removed = SweepObject(longLivedContainer, visited, 0);

      foreach (Assembly library in LongLivedAssemblies().Where(IsSweptLibrary))
      {
        foreach (Type type in StaticCacheHolders(library))
        {
          removed += SweepStatics(type, visited);
        }
      }

      removed += DisposeMetersOfUnloadedCode();

      Log.Info("Dropped {Count} cache entries that held unloaded plugin code", removed);
    }

    /// <summary>
    /// Every meter is listed in the runtime for the life of the process. A library a plugin carries (Npgsql) makes
    /// its meter with callbacks into its own code, and never disposes it.
    /// </summary>
    private static int DisposeMetersOfUnloadedCode()
    {
      FieldInfo? allMetersField = typeof(System.Diagnostics.Metrics.Meter).GetField("s_allMeters", BindingFlags.Static | BindingFlags.NonPublic);
      if (allMetersField?.GetValue(null) is not IList allMeters)
      {
        return 0;
      }

      List<System.Diagnostics.Metrics.Meter> stale;
      lock (allMeters)
      {
        stale = allMeters.OfType<System.Diagnostics.Metrics.Meter>()
          .Where(meter => HoldsCollectible(meter, new HashSet<object>(ReferenceEqualityComparer.Instance), 0))
          .ToList();
      }

      foreach (System.Diagnostics.Metrics.Meter meter in stale)
      {
        meter.Dispose();
      }

      return stale.Count;
    }

    private static void RunMetadataUpdateHandlers()
    {
      foreach (Assembly assembly in LongLivedAssemblies())
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

    /// <summary>NWNX hosts Anvil in a load context of its own, so Anvil's libraries are not in the default one.</summary>
    private static IEnumerable<Assembly> LongLivedAssemblies()
    {
      return AssemblyLoadContext.All.Where(context => !context.IsCollectible).SelectMany(context => context.Assemblies).Distinct();
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

      Type[] attributes = types.Where(type => typeof(Attribute).IsAssignableFrom(type)).Concat(SerializationAttributes).ToArray();
      foreach (Type type in types)
      {
        if (!type.IsGenericTypeDefinition)
        {
          yield return type;
          continue;
        }

        if (type.GetGenericArguments().Length != 1)
        {
          continue;
        }

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

    private static int SweepStatics(Type type, HashSet<object> visited)
    {
      int removed = 0;
      foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
      {
        if (field.IsLiteral || field.FieldType.IsPrimitive || field.FieldType.IsEnum)
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

        removed += SweepObject(value, visited, 0);
      }

      return removed;
    }

    private static int SweepObject(object? value, HashSet<object> visited, int depth)
    {
      if (value == null || depth > MaxDepth || value is Type or Assembly or string || value.GetType().IsPrimitive || !visited.Add(value))
      {
        return 0;
      }

      int removed = 0;
      if (value is IDictionary dictionary)
      {
        removed += RemoveCollectibleEntries(dictionary);
      }

      if (value is Array array)
      {
        foreach (object? element in array)
        {
          removed += SweepObject(element, visited, depth + 1);
        }

        return removed;
      }

      if (!IsWalkable(value.GetType()))
      {
        return removed;
      }

      foreach (FieldInfo field in InstanceFields(value.GetType()))
      {
        if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType.IsPointer)
        {
          continue;
        }

        object? child = field.GetValue(value);
        if (child != null && IsImmutableTree(child.GetType()) && !field.IsInitOnly && HoldsCollectible(child, new HashSet<object>(ReferenceEqualityComparer.Instance), 0))
        {
          object? empty = EmptyOf(child.GetType());
          if (empty != null)
          {
            field.SetValue(value, empty);
            removed++;
            continue;
          }
        }

        removed += SweepObject(child, visited, depth + 1);
      }

      return removed;
    }

    private static int RemoveCollectibleEntries(IDictionary dictionary)
    {
      List<object> stale = [];
      try
      {
        foreach (DictionaryEntry entry in dictionary)
        {
          if (HoldsCollectible(entry.Key, new HashSet<object>(ReferenceEqualityComparer.Instance), 0) || HoldsCollectible(entry.Value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0))
          {
            stale.Add(entry.Key);
          }
        }

        foreach (object key in stale)
        {
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

    private static bool HoldsCollectible(object? value, HashSet<object> visited, int depth)
    {
      switch (value)
      {
        case null:
          return false;
        case Type type:
          return IsCollectible(type);
        case Assembly assembly:
          return assembly.IsCollectible;
        case MemberInfo member:
          return IsCollectible(member.DeclaringType) || IsCollectible(member.ReflectedType);
        case string:
          return false;
      }

      Type valueType = value.GetType();
      if (valueType.IsCollectible)
      {
        return true;
      }

      if (depth > 6 || valueType.IsPrimitive || !visited.Add(value))
      {
        return false;
      }

      if (value is Delegate callback && (IsCollectible(callback.Method.DeclaringType) || HoldsCollectible(callback.Target, visited, depth + 1)))
      {
        return true;
      }

      if (value is IEnumerable sequence and not IDictionary)
      {
        foreach (object? element in sequence)
        {
          if (HoldsCollectible(element, visited, depth + 1))
          {
            return true;
          }
        }
      }

      if (!IsWalkable(valueType) && valueType.Namespace?.StartsWith("System.Reflection") != true && value is not Delegate)
      {
        return false;
      }

      return InstanceFields(valueType).Any(field => !field.FieldType.IsPrimitive && !field.FieldType.IsPointer && HoldsCollectible(field.GetValue(value), visited, depth + 1));
    }

    private static bool IsCollectible(Type? type)
    {
      if (type == null)
      {
        return false;
      }

      return type.IsCollectible || (type.IsGenericType && type.GetGenericArguments().Any(IsCollectible)) || (type.HasElementType && IsCollectible(type.GetElementType()));
    }

    private static bool IsSweptLibrary(Assembly assembly) => SweptLibraries.Contains(assembly.GetName().Name);

    private static bool IsWalkable(Type type)
    {
      if (type.IsCollectible)
      {
        return false;
      }

      string? assemblyName = type.Assembly.GetName().Name;
      return SweptLibraries.Contains(assemblyName) || type.Namespace?.StartsWith("System.Collections") == true || type.Namespace?.StartsWith("System.Runtime.CompilerServices") == true
        || type.Namespace == "System.Diagnostics.Metrics";
    }

    private static bool IsImmutableTree(Type type) => type.Assembly.GetName().Name == "LightInject" && type.Name.StartsWith("ImmutableHash");

    private static object? EmptyOf(Type treeType)
    {
      return treeType.GetField("Empty", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
        ?? treeType.GetProperty("Empty", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
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
  }
}
