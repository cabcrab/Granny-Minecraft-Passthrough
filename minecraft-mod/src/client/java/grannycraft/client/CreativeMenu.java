package grannycraft.client;

import com.google.gson.JsonObject;
import com.mojang.blaze3d.platform.InputConstants;
import grannycraft.GrannyCraft;
import grannycraft.client.mixin.CreativeScreenAccessor;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.inventory.CreativeModeInventoryScreen;
import net.minecraft.client.input.KeyEvent;
import net.minecraft.client.input.MouseButtonInfo;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.Identifier;
import net.minecraft.world.item.CreativeModeTab;

/**
 * Minecraft's creative inventory over Granny (R): every item, opening on the spawn eggs, while Steve stays in survival
 * (GrannyCraft.creativeMenu makes the player count as having infinite materials only while it is open). Minecraft's
 * window is behind Granny's and never gets the mouse: Granny sends hers (position as a fraction of the picture,
 * buttons, wheel) and it goes through Minecraft's own mouse handling, so the screen works as it normally does.
 *
 * <p>Host to Minecraft: {"t":"menu","on":bool}, {"t":"mouse","x":0..1,"y":0..1 (from the top)},
 * {"t":"mbtn","b":0|1|2,"down":bool}, {"t":"mwheel","d":+-1}, typing: {"t":"mtext","s":text} and
 * {"t":"mkey","k":"backspace|delete|left|right|home|end|enter"}. Minecraft to host: {"t":"menu","on":bool,"typing":bool}
 * whenever the screen opens or closes (also when it closes itself) or the search box takes or loses the keyboard.
 */
final class CreativeMenu {
	/** Ticks the creative rights outlive the screen: the last slot changes still reach the server. */
	private static final int LINGER = 10;
	private static boolean reported, reportedTyping;
	private static int lingerIn = -1;

	private CreativeMenu() {
	}

	static boolean open(final Minecraft minecraft) {
		return minecraft.gui.screen() instanceof CreativeModeInventoryScreen;
	}

	/** Client thread. */
	static void handle(final Minecraft minecraft, final JsonObject m) {
		switch (m.get("t").getAsString()) {
			case "menu" -> {
				boolean on = m.get("on").getAsBoolean();
				if (on && !open(minecraft) && minecraft.player != null && minecraft.gui.screen() == null) {
					GrannyCraft.creativeMenu = true;
					lingerIn = -1;
					// it always opens on the spawn eggs (the other tabs are a click away)
					CreativeModeTab eggs = BuiltInRegistries.CREATIVE_MODE_TAB.getValue(Identifier.withDefaultNamespace("spawn_eggs"));
					if (eggs != null) {
						CreativeScreenAccessor.grannycraft$setSelectedTab(eggs);
					}

					minecraft.gui.setScreen(new CreativeModeInventoryScreen(minecraft.player, minecraft.player.connection.enabledFeatures(),
						minecraft.options.operatorItemsTab().get()));
				} else if (!on && open(minecraft)) {
					minecraft.gui.screen().onClose();
				}

				report(minecraft);
			}
			case "mouse" -> {
				var window = minecraft.getWindow();
				double x = Math.clamp(m.get("x").getAsDouble(), 0.0, 1.0) * window.getScreenWidth();
				double y = Math.clamp(m.get("y").getAsDouble(), 0.0, 1.0) * window.getScreenHeight();
				minecraft.mouseHandler.onMove(window.handle(), x, y, 0.0, 0.0);
			}
			case "mbtn" -> {
				if (minecraft.gui.screen() != null) {
					// Unity numbers the buttons 0 left, 1 right, 2 middle; Minecraft (SDL) 1 left, 2 middle, 3 right
					int button = switch (m.get("b").getAsInt()) {
						case 0 -> InputConstants.MOUSE_BUTTON_LEFT;
						case 1 -> InputConstants.MOUSE_BUTTON_RIGHT;
						default -> InputConstants.MOUSE_BUTTON_MIDDLE;
					};
					minecraft.mouseHandler.onButton(minecraft.getWindow().handle(), new MouseButtonInfo(button, 0),
						m.get("down").getAsBoolean() ? InputConstants.PRESS : InputConstants.RELEASE);
				}
			}
			case "mtext" -> {
				if (minecraft.gui.screen() != null) {
					minecraft.keyboardHandler.textInput(minecraft.getWindow().handle(), m.get("s").getAsString());
				}
			}
			case "mkey" -> {
				int key = switch (m.get("k").getAsString()) {
					case "backspace" -> InputConstants.KEY_BACKSPACE;
					case "delete" -> InputConstants.KEY_DELETE;
					case "left" -> InputConstants.KEY_LEFT;
					case "right" -> InputConstants.KEY_RIGHT;
					case "home" -> InputConstants.KEY_HOME;
					case "end" -> InputConstants.KEY_END;
					case "enter" -> InputConstants.KEY_RETURN;
					default -> -1;
				};
				if (key >= 0 && minecraft.gui.screen() != null) {
					minecraft.gui.screen().keyPressed(new KeyEvent(key, key, 0));
				}
			}
			case "mwheel" -> {
				if (minecraft.gui.screen() != null) {
					minecraft.mouseHandler.onScroll(minecraft.getWindow().handle(), 0.0, m.get("d").getAsDouble());
				}
			}
			default -> {
			}
		}
	}

	/** Every client tick: tell the host when the screen comes or goes, and end the creative rights after it closed. */
	static void tick(final Minecraft minecraft) {
		boolean open = open(minecraft);
		if (!open && GrannyCraft.creativeMenu && lingerIn < 0) {
			lingerIn = LINGER;
		}

		if (open) {
			lingerIn = -1;
		} else if (lingerIn > 0 && --lingerIn == 0) {
			GrannyCraft.creativeMenu = false;
			lingerIn = -1;
		}

		if (open != reported || typing(minecraft) != reportedTyping) {
			report(minecraft);
		}
	}

	/** The search box has the keyboard: the host sends what is typed (and its menu key types too). */
	private static boolean typing(final Minecraft minecraft) {
		return open(minecraft) && minecraft.gui.screen().isInputCaptured();
	}

	private static void report(final Minecraft minecraft) {
		reported = open(minecraft);
		reportedTyping = typing(minecraft);
		GrannyCraft.events.accept("{\"t\":\"menu\",\"on\":" + reported + ",\"typing\":" + reportedTyping + "}");
	}

	/** The host reconnected: it needs the state again. */
	static void resend() {
		reported = !reported;
	}
}
