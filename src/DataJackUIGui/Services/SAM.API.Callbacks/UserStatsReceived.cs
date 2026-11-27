using SAM.API.Types;

namespace SAM.API.Callbacks;

public class UserStatsReceived : Callback<SAM.API.Types.UserStatsReceived>
{
    public override int Id => 1101;

    public override bool IsServer => false;
}
