package grannycraft.mixin;

import grannycraft.GhostBlock;
import grannycraft.GrannyCraft;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.pathfinder.PathType;
import net.minecraft.world.level.pathfinder.WalkNodeEvaluator;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Mobs walk Granny's house: her floors and walls are judged by their real shape in each cell (GhostBlock.pathType). */
@Mixin(WalkNodeEvaluator.class)
abstract class WalkNodeEvaluatorMixin {
	@Inject(method = "getPathTypeFromState(Lnet/minecraft/world/level/BlockGetter;Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/level/pathfinder/PathType;", at = @At("HEAD"), cancellable = true)
	private static void grannycraft$ghostPathType(final BlockGetter level, final BlockPos pos, final CallbackInfoReturnable<PathType> cir) {
		if (level.getBlockState(pos).is(GrannyCraft.GHOST)) {
			cir.setReturnValue(GhostBlock.pathType(pos));
		}
	}
}
