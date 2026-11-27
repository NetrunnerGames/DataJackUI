using System.Runtime.InteropServices;

namespace SAM.API;

public abstract class Callback : ICallback
{
    public delegate void CallbackFunction(nint param);

    public abstract int Id { get; }

    public abstract bool IsServer { get; }

    public event CallbackFunction? OnRun;

    public void Run(nint param)
    {
        this.OnRun?.Invoke(param);
    }
}
public abstract class Callback<TParameter> : ICallback where TParameter : struct
{
    public delegate void CallbackFunction(TParameter arg);

    public abstract int Id { get; }

    public abstract bool IsServer { get; }

    public event CallbackFunction? OnRun;

    public void Run(nint pvParam)
    {
        object? boxed = Marshal.PtrToStructure(pvParam, typeof(TParameter));
        if (boxed is TParameter arg)
        {
            this.OnRun?.Invoke(arg);
        }
    }
}
