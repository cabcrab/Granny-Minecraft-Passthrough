package grannycraft.client;

import com.google.gson.JsonObject;
import grannycraft.GrannyCraft;
import grannycraft.client.mixin.KeyMappingAccessor;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.entity.player.Inventory;
import org.lwjgl.sdl.SDLVideo;

/** Host input, applied on the client thread: the host window has the focus, so Minecraft never sees these itself. */
final class ClientInput {
	private ClientInput() {
	}

	static void handle(final Minecraft minecraft, final JsonObject m) {
		LocalPlayer player = minecraft.player;
		switch (m.get("t").getAsString()) {
			case "key" -> {
				String k = m.get("k").getAsString();
				boolean down = !m.has("down") || m.get("down").getAsBoolean();
				if (k.equals("escape")) {
					if (down && minecraft.gui.screen() != null) {
						minecraft.gui.screen().onClose();
					}

					return;
				}

				KeyMapping key = switch (k) {
					case "use" -> minecraft.options.keyUse;
					case "attack" -> minecraft.options.keyAttack;
					case "pick" -> minecraft.options.keyPickItem;
					case "inventory" -> minecraft.options.keyInventory;
					case "drop" -> minecraft.options.keyDrop;
					case "swap" -> minecraft.options.keySwapOffhand;
					default -> null;
				};
				if (k.equals("attack") && down && player != null && minecraft.gui.screen() == null) {
					// a swing: Granny looks along her camera for Granny/Grandpa within reach, with Minecraft's damage
					// (the held item's attack damage, scaled by how charged the attack is, like a real Minecraft hit)
					float charge = player.getAttackStrengthScale(0.5F);
					// the held item's attack bonus is on the server's copy of the player (the client one only has the base)
					double attack = player.getAttributeValue(Attributes.ATTACK_DAMAGE);
					var server = minecraft.getSingleplayerServer();
					if (server != null && !server.getPlayerList().getPlayers().isEmpty()) {
						attack = server.getPlayerList().getPlayers().get(0).getAttributeValue(Attributes.ATTACK_DAMAGE);
					}

					double damage = attack * (0.2 + charge * charge * 0.8);
					boolean crit = charge > 0.9F && player.fallDistance > 0.0F && !player.onGround();
					if (crit) {
						damage *= 1.5;
					}

					GrannyCraft.events.accept(String.format(java.util.Locale.ROOT, "{\"t\":\"melee\",\"d\":%.2f,\"reach\":%.2f,\"crit\":%b,\"item\":\"%s\"}",
						damage, player.entityInteractionRange(), crit, BuiltInRegistries.ITEM.getKey(player.getMainHandItem().getItem()).getPath()));
				}

				if (key != null) {
					if (down && !key.isDown()) {
						KeyMappingAccessor access = (KeyMappingAccessor)key;
						access.grannycraft$setClickCount(access.grannycraft$getClickCount() + 1);
					}

					key.setDown(down);
				}
			}
			case "menu", "mouse", "mbtn", "mwheel", "mtext", "mkey" -> CreativeMenu.handle(minecraft, m);
			case "slot" -> {
				if (player != null) {
					player.getInventory().setSelectedSlot(Math.clamp(m.get("n").getAsInt(), 0, Inventory.getSelectionSize() - 1));
				}
			}
			case "scroll" -> {
				if (player != null) {
					Inventory inventory = player.getInventory();
					int size = Inventory.getSelectionSize();
					inventory.setSelectedSlot(Math.floorMod(inventory.getSelectedSlot() - m.get("d").getAsInt(), size));
				}
			}
			case "hud" -> {
				if (minecraft.gui.hud.isHidden() != m.get("hidden").getAsBoolean()) {
					minecraft.gui.hud.toggle();
				}
			}
			case "view" -> {
				// match the host's picture exactly: un-minimize/un-maximize first (resizing a maximized window is ignored)
				int w = m.get("w").getAsInt(), h = m.get("h").getAsInt();
				long handle = minecraft.getWindow().handle();
				SDLVideo.SDL_RestoreWindow(handle);
				minecraft.getWindow().setWindowed(w, h);
				SDLVideo.SDL_SetWindowSize(handle, w, h);
				SDLVideo.SDL_SyncWindow(handle);
			}
			default -> {
			}
		}
	}
}
