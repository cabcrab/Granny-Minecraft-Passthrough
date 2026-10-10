package grannycraft.client.mixin;

import grannycraft.GrannyCraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;
import net.minecraft.client.Minecraft;
import net.minecraft.client.MouseHandler;

/**
 * Granny's window has the focus, so Minecraft never grabs the mouse; but holding attack (breaking a block) and using
 * items only work while it is grabbed. While attached, Minecraft acts as if it were, except while a screen is open
 * (the creative inventory): screens follow the pointer Granny sends.
 */
@Mixin(MouseHandler.class)
abstract class MouseHandlerMixin {
	@Inject(method = "isMouseGrabbed()Z", at = @At("HEAD"), cancellable = true)
	private void grannycraft$grabbed(final CallbackInfoReturnable<Boolean> cir) {
		if (GrannyCraft.active && Minecraft.getInstance().gui.screen() == null) {
			cir.setReturnValue(true);
		}
	}
}
