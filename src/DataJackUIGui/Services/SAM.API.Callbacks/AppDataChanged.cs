using SAM.API.Types;

namespace SAM.API.Callbacks;

public class AppDataChanged : Callback<SAM.API.Types.AppDataChanged>
{
    public override int Id => 1001;

    public override bool IsServer => false;
}
