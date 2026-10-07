package grannycraft.client.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import grannycraft.GrannyCraft;
import net.minecraft.client.renderer.ScreenEffectRenderer;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.resources.Identifier;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Granny's camera can pass through a Minecraft block (third person against a wall, crouching under a block you
 * placed): Minecraft would then cover the whole screen with that block's texture. Not while attached.
 */
@Mixin(ScreenEffectRenderer.class)
abstract class ScreenEffectMixin {
	@Inject(method = "submitBlockSprite", at = @At("HEAD"), cancellable = true)
	private static void grannycraft$noInWallOverlay(final Identifier sprite, final float a, final float b, final float c, final float d,
		final PoseStack pose, final SubmitNodeCollector collector, final int light, final CallbackInfo ci) {
		if (GrannyCraft.active) {
			ci.cancel();
		}
	}
}
