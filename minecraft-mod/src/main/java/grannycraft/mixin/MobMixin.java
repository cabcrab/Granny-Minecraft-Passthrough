package grannycraft.mixin;

import grannycraft.StandIn;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.ai.goal.GoalSelector;
import net.minecraft.world.entity.ai.goal.target.NearestAttackableTargetGoal;
import net.minecraft.world.entity.monster.Enemy;
import net.minecraft.world.level.Level;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Every hostile mob goes for Granny and Grandpa (their stand-ins) when it sees them, before the player: a zombie
 * spawned from an egg in the house runs at Granny as soon as she is in sight. Brain-driven mobs (piglins, hoglins)
 * don't use these goals and keep to Minecraft's own targets.
 */
@Mixin(Mob.class)
abstract class MobMixin {
	@Shadow
	@Final
	protected GoalSelector targetSelector;

	@Inject(method = "<init>", at = @At("TAIL"))
	private void grannycraft$huntGranny(final EntityType<? extends Mob> type, final Level level, final CallbackInfo ci) {
		Mob self = (Mob) (Object) this;
		if (self instanceof Enemy && !(self instanceof StandIn) && level != null && !level.isClientSide()) {
			this.targetSelector.addGoal(1, new NearestAttackableTargetGoal<>(self, StandIn.class, true));
		}
	}
}
