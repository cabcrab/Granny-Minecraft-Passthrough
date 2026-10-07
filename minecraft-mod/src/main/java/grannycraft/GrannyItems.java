package grannycraft;

import net.minecraft.core.component.DataComponents;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.network.chat.Component;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.util.Prediction;
import net.minecraft.world.entity.player.Inventory;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.item.component.CustomData;

/**
 * The item Granny's player holds, as a hotbar item: picking up one of Granny's items puts a token named after it in
 * the selected slot (whatever Minecraft item was there is dropped on the floor). Granny holds one item at a time, so
 * there is at most one token. It goes when Granny's item leaves the hand (dropped, used, taken).
 * The token is drawn by Granny (her own model of the item): Minecraft doesn't draw it in the first-person hand.
 */
public final class GrannyItems {
	private static final String KEY = "grannycraft_item";

	private GrannyItems() {
	}

	public static boolean isToken(final ItemStack stack) {
		if (stack == null || stack.isEmpty()) {
			return false;
		}

		CustomData data = stack.get(DataComponents.CUSTOM_DATA);
		return data != null && data.copyTag().contains(KEY);
	}

	private static ServerPlayer player(final MinecraftServer s) {
		return s.getPlayerList().getPlayers().isEmpty() ? null : s.getPlayerList().getPlayers().get(0);
	}

	/** Granny's player picked up `name`: it goes in the selected slot. */
	public static void give(final String name) {
		MinecraftServer s = WorldBridge.server();
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerPlayer p = player(s);
			if (p == null) {
				return;
			}

			Inventory inv = p.getInventory();
			int sel = inv.getSelectedSlot();
			for (int i = 0; i < inv.getContainerSize(); i++) {
				if (isToken(inv.getItem(i))) {
					inv.setItem(i, ItemStack.EMPTY);
				}
			}

			ItemStack current = inv.getItem(sel);
			if (!current.isEmpty()) {
				// what was in the hand goes to the floor, as when Minecraft's player drops an item
				p.drop(current.copy(), false, Prediction.SERVER_ONLY);
			}

			ItemStack token = new ItemStack(Items.PAPER);
			token.set(DataComponents.CUSTOM_NAME, Component.literal(name));
			CompoundTag tag = new CompoundTag();
			tag.putString(KEY, name);
			token.set(DataComponents.CUSTOM_DATA, CustomData.of(tag));
			inv.setItem(sel, token);
			p.containerMenu.broadcastChanges();
			GrannyCraft.LOG.info("granny item {} in slot {}", name, sel + 1);
		});
	}

	/** Granny's item left the hand: its slot is empty again. */
	public static void clear() {
		MinecraftServer s = WorldBridge.server();
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerPlayer p = player(s);
			if (p == null) {
				return;
			}

			Inventory inv = p.getInventory();
			for (int i = 0; i < inv.getContainerSize(); i++) {
				if (isToken(inv.getItem(i))) {
					inv.setItem(i, ItemStack.EMPTY);
				}
			}

			p.containerMenu.broadcastChanges();
		});
	}
}
