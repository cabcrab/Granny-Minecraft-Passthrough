package grannycraft.mixin;

import net.minecraft.world.entity.player.Inventory;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * There is no inventory screen over Granny: items only go into the 9 hotbar slots (and the off hand, where the arrows
 * are). With a full hotbar an item on the floor stays there, as with a full inventory in Minecraft.
 */
@Mixin(Inventory.class)
abstract class HotbarOnlyMixin {
	private static boolean grannycraft$hidden(final int slot) {
		return slot >= Inventory.getSelectionSize() && slot != Inventory.SLOT_OFFHAND;
	}

	@Inject(method = "getFreeSlot()I", at = @At("RETURN"), cancellable = true)
	private void grannycraft$freeHotbarOnly(final CallbackInfoReturnable<Integer> cir) {
		if (grannycraft$hidden(cir.getReturnValueI())) {
			cir.setReturnValue(-1);
		}
	}

	@Inject(method = "getSlotWithRemainingSpace(Lnet/minecraft/world/item/ItemStack;)I", at = @At("RETURN"), cancellable = true)
	private void grannycraft$stackHotbarOnly(final CallbackInfoReturnable<Integer> cir) {
		if (grannycraft$hidden(cir.getReturnValueI())) {
			cir.setReturnValue(-1);
		}
	}
}
