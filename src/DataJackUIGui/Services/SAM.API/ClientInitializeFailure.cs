namespace SAM.API;

public enum ClientInitializeFailure : byte
{
    Unknown,
    GetInstallPath,
    Load,
    CreateSteamClient,
    CreateSteamPipe,
    ConnectToGlobalUser,
    AppIdMismatch
}
