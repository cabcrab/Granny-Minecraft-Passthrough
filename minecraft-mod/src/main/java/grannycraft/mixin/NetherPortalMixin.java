package grannycraft.mixin;

import grannycraft.GrannyCraft;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.InsideBlockEffectApplier;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.NetherPortalBlock;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Over Granny a portal never takes anyone to the Nether (Steve stays in Granny's house, and the portal's purple swirl
 * would cover Granny's picture): walking in brings the Nether here instead (NetherZone).
 */
@Mixin(NetherPortalBlock.class)
abstract class NetherPortalMixin {
	@Inject(method = "entityInside", at = @At("HEAD"), cancellable = true)
	private void grannycraft$noTravel(final BlockState state, final Level level, final BlockPos pos, final Entity entity,
		final InsideBlockEffectApplier effects, final boolean precise, final CallbackInfo ci) {
		if (GrannyCraft.active) {
			ci.cancel();
		}
	}
}
