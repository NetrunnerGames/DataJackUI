using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SAM.API.Types;
using SAM.API.Wrappers;

namespace SAM.API;

public class Client : IDisposable
{
    public SteamClient018? SteamClient;

    public SteamUser012? SteamUser;

    public SteamUserStats013? SteamUserStats;

    public SteamUtils005? SteamUtils;

    public SteamApps001? SteamApps001;

    public SteamApps008? SteamApps008;

    private bool _IsDisposed;

    private int _Pipe;

    private int _User;

    private readonly List<ICallback> _Callbacks = new List<ICallback>();

    private bool _RunningCallbacks;

    public void Initialize(long appId)
    {
        if (string.IsNullOrEmpty(Steam.GetInstallPath()))
        {
            throw new ClientInitializeException(ClientInitializeFailure.GetInstallPath, "failed to get Steam install path");
        }
        if (appId != 0L)
        {
            Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(CultureInfo.InvariantCulture));
        }
        if (!Steam.Load())
        {
            throw new ClientInitializeException(ClientInitializeFailure.Load, "failed to load SteamClient");
        }
        SteamClient = Steam.CreateInterface<SteamClient018>("SteamClient018");
        if (SteamClient == null)
        {
            throw new ClientInitializeException(ClientInitializeFailure.CreateSteamClient, "failed to create ISteamClient018");
        }
        _Pipe = SteamClient.CreateSteamPipe();
        if (_Pipe == 0)
        {
            throw new ClientInitializeException(ClientInitializeFailure.CreateSteamPipe, "failed to create pipe");
        }
        _User = SteamClient.ConnectToGlobalUser(_Pipe);
        if (_User == 0)
        {
            throw new ClientInitializeException(ClientInitializeFailure.ConnectToGlobalUser, "failed to connect to global user");
        }
        SteamUtils = SteamClient.GetSteamUtils004(_Pipe);
        if (appId > 0 && SteamUtils.GetAppId() != (uint)appId)
        {
            throw new ClientInitializeException(ClientInitializeFailure.AppIdMismatch, "appID mismatch");
        }
        SteamUser = SteamClient.GetSteamUser012(_User, _Pipe);
        SteamUserStats = SteamClient.GetSteamUserStats013(_User, _Pipe);
    }

    ~Client()
    {
        Dispose(disposing: false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_IsDisposed)
        {
            return;
        }
        if (SteamClient != null && _Pipe > 0)
        {
            if (_User > 0)
            {
                SteamClient.ReleaseUser(_Pipe, _User);
                _User = 0;
            }
            SteamClient.ReleaseSteamPipe(_Pipe);
            _Pipe = 0;
        }
        _IsDisposed = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public TCallback CreateAndRegisterCallback<TCallback>() where TCallback : ICallback, new()
    {
        TCallback val = new TCallback();
        _Callbacks.Add(val);
        return val;
    }

    public void RunCallbacks(bool server)
    {
        if (_RunningCallbacks)
        {
            return;
        }
        _RunningCallbacks = true;
        CallbackMessage message;
        int call;
        while (Steam.GetCallback(_Pipe, out message, out call))
        {
            int callbackId = message.Id;
            foreach (ICallback item in _Callbacks.Where((ICallback candidate) => candidate.Id == callbackId && candidate.IsServer == server))
            {
                item.Run(message.ParamPointer);
            }
            Steam.FreeLastCallback(_Pipe);
        }
        _RunningCallbacks = false;
    }
}
