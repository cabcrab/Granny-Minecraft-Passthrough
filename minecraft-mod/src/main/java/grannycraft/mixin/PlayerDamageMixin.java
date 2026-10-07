package grannycraft.mixin;

import grannycraft.GrannyCraft;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.tags.DamageTypeTags;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Steve is moved by Granny every tick: no fall, wall or void damage while attached (explosions still hurt). */
@Mixin(LivingEntity.class)
abstract class PlayerDamageMixin {
	@Inject(method = "hurtServer(Lnet/minecraft/server/level/ServerLevel;Lnet/minecraft/world/damagesource/DamageSource;F)Z", at = @At("HEAD"), cancellable = true)
	private void grannycraft$noEnvironmentDamage(final ServerLevel level, final DamageSource source, final float amount, final CallbackInfoReturnable<Boolean> cir) {
		if (GrannyCraft.active && (Object) this instanceof ServerPlayer
			&& (source.is(DamageTypeTags.IS_FALL) || source.is(DamageTypeTags.BYPASSES_ARMOR) && !source.is(DamageTypeTags.IS_EXPLOSION)
				|| source.getMsgId().equals("inWall") || source.getMsgId().equals("cramming"))) {
			cir.setReturnValue(false);
		}
	}
}
