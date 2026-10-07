package grannycraft.client.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import grannycraft.GrannyItems;
import net.minecraft.client.renderer.FirstPersonHandsAndItemsRenderer;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.state.level.FirstPersonHandsAndItemsRenderState;
import net.minecraft.client.renderer.state.level.PlayerRenderState;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.item.ItemStack;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Granny's item in the hand is drawn by Granny (her own model): Minecraft draws neither the item nor the arm then. */
@Mixin(FirstPersonHandsAndItemsRenderer.class)
abstract class FirstPersonItemMixin {
	@Inject(method = "submitArmWithItem", at = @At("HEAD"), cancellable = true)
	private void grannycraft$grannyItem(final PlayerRenderState player, final FirstPersonHandsAndItemsRenderState state, final float a, final float b,
		final InteractionHand hand, final float c, final ItemStack stack, final float d, final PoseStack pose, final SubmitNodeCollector collector,
		final int light, final CallbackInfo ci) {
		// the arrows kept in the off hand (for the bow) aren't drawn in the left hand
		if (GrannyItems.isToken(stack) || hand == InteractionHand.OFF_HAND && stack.is(net.minecraft.world.item.Items.ARROW)) {
			ci.cancel();
		}
	}
}
