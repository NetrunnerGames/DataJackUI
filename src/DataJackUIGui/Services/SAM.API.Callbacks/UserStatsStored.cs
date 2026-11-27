using SAM.API.Types;

namespace SAM.API.Callbacks;

public class UserStatsStored : Callback<SAM.API.Types.UserStatsStored>
{
    public override int Id => 1102;

    public override bool IsServer => false;
}
