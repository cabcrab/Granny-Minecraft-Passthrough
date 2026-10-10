package grannycraft.mixin;

import grannycraft.GrannyCraft;
import net.minecraft.world.entity.player.Player;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * While the creative inventory is open over Granny (R), the player may take any item from it, as in creative mode:
 * the screen and the server both ask hasInfiniteMaterials. The game mode itself stays survival (health, hunger,
 * damage, mining speed).
 */
@Mixin(Player.class)
abstract class CreativeMenuMixin {
	@Inject(method = "hasInfiniteMaterials()Z", at = @At("HEAD"), cancellable = true)
	private void grannycraft$creativeMenu(final CallbackInfoReturnable<Boolean> cir) {
		if (GrannyCraft.creativeMenu) {
			cir.setReturnValue(true);
		}
	}
}
