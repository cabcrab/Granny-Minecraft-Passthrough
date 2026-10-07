package grannycraft.client.mixin;

import grannycraft.client.HostState;
import net.minecraft.client.Camera;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Aim along Granny's camera (what is under the crosshair): in first person the camera is Granny's eyes, which sit a
 * little above Steve's at this scale; behind Steve (third person) the ray starts level with him. In the front view
 * Steve aims with his own eyes, like Minecraft. (Adapted from universal-modder's passthrough, MIT.)
 */
@Mixin(Entity.class)
abstract class EntityPickMixin {
	@Inject(method = "pick(DFZ)Lnet/minecraft/world/phys/HitResult;", at = @At("HEAD"), cancellable = true)
	private void grannycraft$cameraRay(final double range, final float a, final boolean withLiquids, final CallbackInfoReturnable<HitResult> cir) {
		HostState.Pose p = HostState.frame();
		if (p == null || !((Object) this instanceof LocalPlayer self)) {
			return;
		}

		Camera camera = Minecraft.getInstance().gameRenderer.mainCamera();
		Vec3 from = camera.position();
		Vec3 dir = new Vec3(camera.forwardVector()).normalize();
		Vec3 start;
		Vec3 end;
		if (p.firstPerson()) {
			start = from;
			end = from.add(dir.scale(range));
		} else {
			Vec3 eye = self.getEyePosition(a);
			if (eye.subtract(from).dot(dir) < 0.0) {
				return; // front view: the camera looks at Steve, he aims himself
			}

			double along = Math.max(0.0, eye.subtract(from).dot(dir));
			start = from.add(dir.scale(Math.max(0.0, along - 0.4)));
			end = from.add(dir.scale(along + range));
		}

		ClipContext.Fluid fluid = withLiquids ? ClipContext.Fluid.ANY : ClipContext.Fluid.NONE;
		cir.setReturnValue(self.level().clip(new ClipContext(start, end, ClipContext.Block.OUTLINE, fluid, self)));
	}
}
