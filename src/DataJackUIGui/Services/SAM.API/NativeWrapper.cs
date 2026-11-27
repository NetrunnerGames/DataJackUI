using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SAM.API;

public abstract class NativeWrapper<TNativeFunctions> : INativeWrapper
{
    protected nint ObjectAddress;

    protected TNativeFunctions Functions = default!;

    private readonly Dictionary<nint, Delegate> _FunctionCache = new Dictionary<nint, Delegate>();

    public override string ToString()
    {
        return $"Steam Interface<{typeof(TNativeFunctions)}> #{((IntPtr)ObjectAddress).ToInt32():X8}";
    }

    public void SetupFunctions(nint objectAddress)
    {
        ObjectAddress = objectAddress;
        var nativeClass = Marshal.PtrToStructure<NativeClass>(ObjectAddress);
        Functions = Marshal.PtrToStructure<TNativeFunctions>(nativeClass.VirtualTable)!;
    }

    protected Delegate GetDelegate<TDelegate>(nint pointer)
    {
        if (!_FunctionCache.TryGetValue(pointer, out var value))
        {
            value = Marshal.GetDelegateForFunctionPointer(pointer, typeof(TDelegate));
            _FunctionCache[pointer] = value;
        }
        return value;
    }

    protected TDelegate GetFunction<TDelegate>(nint pointer) where TDelegate : class
    {
        return (TDelegate)(object)GetDelegate<TDelegate>(pointer);
    }

    protected void Call<TDelegate>(nint pointer, params object[] args)
    {
        GetDelegate<TDelegate>(pointer).DynamicInvoke(args);
    }

    protected TReturn Call<TReturn, TDelegate>(nint pointer, params object[] args)
    {
        return (TReturn)GetDelegate<TDelegate>(pointer).DynamicInvoke(args)!;
    }
}
