package grannycraft.client.mixin;

import net.minecraft.client.gui.screens.inventory.CreativeModeInventoryScreen;
import net.minecraft.world.item.CreativeModeTab;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

/** The tab the creative inventory opens on (Minecraft remembers the last one in this static field). */
@Mixin(CreativeModeInventoryScreen.class)
public interface CreativeScreenAccessor {
	@Accessor("selectedTab")
	static void grannycraft$setSelectedTab(final CreativeModeTab tab) {
		throw new AssertionError();
	}
}
