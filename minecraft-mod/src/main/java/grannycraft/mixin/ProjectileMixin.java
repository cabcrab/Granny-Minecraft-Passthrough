package grannycraft.mixin;

import grannycraft.StandIn;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.projectile.Projectile;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Only mobs' projectiles hit Granny's stand-ins: the player's arrows fly on through them, since Granny's side already
 * traces those against her real body (WorldBridge.reportArrows).
 */
@Mixin(Projectile.class)
abstract class ProjectileMixin {
	@Shadow
	public abstract Entity getOwner();

	@Inject(method = "canHitEntity", at = @At("HEAD"), cancellable = true)
	private void grannycraft$onlyMobsHitStandIns(final Entity target, final CallbackInfoReturnable<Boolean> cir) {
		if (target instanceof StandIn && !(this.getOwner() instanceof Mob)) {
			cir.setReturnValue(false);
		}
	}
}
