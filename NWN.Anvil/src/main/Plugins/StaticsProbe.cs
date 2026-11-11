using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Anvil.Plugins
{
  /// <summary>
  /// Tells whether a type's static constructor has finished and its statics are set up, by asking the runtime's own
  /// record of the type. Reading a static field by reflection is no way to find out: on a type nothing used yet it runs
  /// the constructor.
  /// </summary>
  internal sealed unsafe class StaticsProbe
  {
    private const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly int recordOffset;
    private readonly int flagsOffset;
    private readonly FieldInfo flags;
    private readonly PropertyInfo ready;
    private readonly object recordCopy;

    private StaticsProbe(Type record, int recordOffset, FieldInfo flags, PropertyInfo ready)
    {
      this.recordOffset = recordOffset;
      this.flags = flags;
      this.ready = ready;
      flagsOffset = (int)Marshal.OffsetOf(record, flags.Name);
      recordCopy = RuntimeHelpers.GetUninitializedObject(record);
    }

    /// <returns>Null when this runtime keeps its record of a type somewhere this Anvil does not know.</returns>
    public static StaticsProbe? TryCreate()
    {
      try
      {
        Assembly coreLib = typeof(object).Assembly;
        Type? record = coreLib.GetType("System.Runtime.CompilerServices.MethodTableAuxiliaryData");
        FieldOffsetAttribute? recordAt = coreLib.GetType("System.Runtime.CompilerServices.MethodTable")?.GetField("AuxiliaryData", AnyMember)?.GetCustomAttribute<FieldOffsetAttribute>();
        FieldInfo? flags = record?.GetField("Flags", AnyMember);
        PropertyInfo? ready = record?.GetProperty("IsClassInitedAndActive", AnyMember);
        if (record == null || recordAt == null || flags?.FieldType != typeof(uint) || ready?.PropertyType != typeof(bool))
        {
          return null;
        }

        return new StaticsProbe(record, recordAt.Value, flags, ready);
      }
      catch (Exception)
      {
        return null;
      }
    }

    public bool AreReady(Type type)
    {
      byte* record = *(byte**)((byte*)type.TypeHandle.Value + recordOffset);
      flags.SetValue(recordCopy, Volatile.Read(ref *(uint*)(record + flagsOffset)));
      return (bool)ready.GetValue(recordCopy)!;
    }
  }
}
