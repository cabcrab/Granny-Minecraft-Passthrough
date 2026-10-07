package grannycraft.client;

import grannycraft.GrannyCraft;
import grannycraft.WorldBridge;
import java.util.List;
import java.util.Optional;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientLifecycleEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.fabric.api.networking.v1.ServerPlayConnectionEvents;
import net.minecraft.client.CloudStatus;
import net.minecraft.client.InactivityFpsLimit;
import net.minecraft.client.Minecraft;
import net.minecraft.client.OptionInstance;
import net.minecraft.client.Options;
import net.minecraft.client.gui.screens.DeathScreen;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.client.tutorial.TutorialSteps;
import net.minecraft.core.HolderLookup;
import net.minecraft.core.HolderSet;
import net.minecraft.core.registries.Registries;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.LevelSettings;
import net.minecraft.world.level.WorldDataConfiguration;
import net.minecraft.world.level.biome.Biomes;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.levelgen.FlatLevelSource;
import net.minecraft.world.level.levelgen.WorldDimensions;
import net.minecraft.world.level.levelgen.WorldOptions;
import net.minecraft.world.level.levelgen.flat.FlatLayerInfo;
import net.minecraft.world.level.levelgen.flat.FlatLevelGeneratorSettings;
import net.minecraft.world.level.levelgen.presets.WorldPresets;

/** Opens (or creates) the void world, sets Minecraft up to sit behind Granny, hands out the kit. */
public class GrannyCraftClient implements ClientModInitializer {
	/** Granny's house is the only "terrain": an empty (void) world, so only what the player builds is Minecraft. */
	private static final String WORLD = "grannycraft";
	/**
	 * Run on the server once the player has joined. Survival (so the HUD shows health and hunger like normal
	 * Minecraft), never hungry, no mobs of its own, always noon, and a kit: sword, bow and arrows, TNT, blocks.
	 */
	private static final List<String> SETUP = List.of(
		"gamemode survival @a",
		"gamerule advance_time false",
		"gamerule advance_weather false",
		"gamerule spawn_mobs false",
		"gamerule spawn_monsters false",
		"gamerule spawn_patrols false",
		"gamerule spawn_phantoms false",
		"gamerule spawn_wandering_traders false",
		"gamerule send_command_feedback false",
		"gamerule log_admin_commands false",
		"gamerule keep_inventory true",
		"gamerule show_advancement_messages false",
		"gamerule player_movement_check false",
		"gamerule fire_spread_radius_around_player 0",
		"difficulty normal",
		"time set noon",
		"weather clear",
		"effect give @a minecraft:saturation infinite 255 true",
		// the void world (and Granny's house as ghost blocks) is dark where Steve stands: full daylight on the hand,
		// items and blocks, like normal Minecraft by day (Granny's compositor matches blocks to her lighting)
		"effect give @a minecraft:night_vision infinite 0 true",
		"clear @a",
		"item replace entity @a hotbar.0 with minecraft:diamond_sword",
		"item replace entity @a hotbar.1 with minecraft:bow[enchantments={power:5,infinity:1}]",
		"item replace entity @a hotbar.2 with minecraft:tnt 64",
		"item replace entity @a hotbar.3 with minecraft:flint_and_steel",
		"item replace entity @a hotbar.4 with minecraft:oak_planks 64",
		"item replace entity @a hotbar.5 with minecraft:cobblestone 64",
		"item replace entity @a hotbar.6 with minecraft:glass 64",
		"item replace entity @a hotbar.7 with minecraft:crossbow[enchantments={multishot:1,quick_charge:3}]",
		"item replace entity @a hotbar.8 with minecraft:diamond_pickaxe[enchantments={efficiency:5}]",
		// no inventory over Granny: only the hotbar, and the arrows in the off hand (bow and crossbow need them)
		"item replace entity @a weapon.offhand with minecraft:arrow 64"
	);
	private static boolean configured;
	private static boolean worldRequested;
	private static int setupIn = -1;
	private static int respawnIn;

	@Override
	public void onInitializeClient() {
		HostLink.launch();
		ClientTickEvents.END_CLIENT_TICK.register(GrannyCraftClient::tick);
		ServerPlayConnectionEvents.JOIN.register((handler, sender, server) -> setupIn = 10);
		ServerTickEvents.END_SERVER_TICK.register(server -> {
			if (setupIn > 0 && --setupIn == 0) {
				SETUP.forEach(WorldBridge::command);
			}
		});
	}

	/** Re-run the setup (a fresh kit), e.g. from Granny's side. */
	public static void resetKit() {
		SETUP.forEach(WorldBridge::command);
	}

	private static int lastSel = -2, lastToken = -2;

	/** Granny side needs the selection again (it reconnected). */
	public static void hotbarChanged() {
		lastSel = lastToken = -2;
	}

	/** The selected slot and the slot holding Granny's item: Granny shows her item only while it is selected. */
	private static void reportHotbar(final Minecraft minecraft) {
		if (minecraft.player == null) {
			return;
		}

		var inv = minecraft.player.getInventory();
		int sel = inv.getSelectedSlot();
		int token = -1;
		for (int i = 0; i < 9; i++) {
			if (grannycraft.GrannyItems.isToken(inv.getItem(i))) {
				token = i;
				break;
			}
		}

		if (sel != lastSel || token != lastToken) {
			lastSel = sel;
			lastToken = token;
			GrannyCraft.events.accept("{\"t\":\"hotbar\",\"sel\":" + sel + ",\"token\":" + token + "}");
		}
	}

	private static void tick(final Minecraft minecraft) {
		reportHotbar(minecraft);
		// a death (TNT) would leave the death screen over Granny's picture: respawn straight away
		if (minecraft.player != null && minecraft.gui.screen() instanceof DeathScreen && --respawnIn <= 0) {
			respawnIn = 40;
			minecraft.player.respawn();
		}

		if (!configured) {
			configured = true;
			configure(minecraft.options);
		}

		if (!worldRequested && minecraft.level == null && minecraft.gui.screen() instanceof TitleScreen) {
			worldRequested = true;
			openWorld(minecraft);
		}
	}

	/** The player's own values of the options changed below: put back (and saved) when Minecraft closes. */
	private static final List<Runnable> RESTORE = new java.util.ArrayList<>();

	private static <T> void set(final OptionInstance<T> option, final T value) {
		T old = option.get();
		RESTORE.add(() -> option.set(old));
		option.set(value);
	}

	/**
	 * Settings for sitting behind Granny: keep running unfocused, no sky/cloud/bobbing effects in the picture. Only for
	 * this session: the game folder may be the player's normal one, so their own settings come back when Minecraft closes.
	 */
	private static void configure(final Options options) {
		boolean pause = options.pauseOnLostFocus;
		RESTORE.add(() -> options.pauseOnLostFocus = pause);
		options.pauseOnLostFocus = false;
		options.onboardAccessibility = false;
		options.tutorialStep = TutorialSteps.NONE;
		set(options.cloudStatus(), CloudStatus.OFF);
		set(options.bobView(), false);
		set(options.vignette(), false);
		set(options.improvedTransparency(), false);
		set(options.inactivityFpsLimit(), InactivityFpsLimit.MINIMIZED);
		set(options.fovEffectScale(), 0.0);
		set(options.damageTiltStrength(), 0.0);
		set(options.menuBackgroundBlurriness(), 0);
		set(options.enableVsync(), false);
		// Minecraft's frame is composited into Granny's as soon as it is ready: the faster it comes, the less it lags
		set(options.framerateLimit(), 240);
		set(options.renderDistance(), 8);
		// Minecraft's sounds (swings, blocks, TNT) stay; its music would fight Granny's horror ambience
		set(options.getSoundSourceOptionInstance(net.minecraft.sounds.SoundSource.MUSIC), 0.0);
		ClientLifecycleEvents.CLIENT_STOPPING.register(minecraft -> {
			RESTORE.forEach(Runnable::run);
			minecraft.options.save();
		});
	}

	private static void openWorld(final Minecraft minecraft) {
		if (minecraft.getLevelSource().levelExists(WORLD)) {
			GrannyCraft.LOG.info("opening world {}", WORLD);
			minecraft.createWorldOpenFlows().openWorld(WORLD, () -> minecraft.gui.setScreen(new TitleScreen()));
		} else {
			GrannyCraft.LOG.info("creating world {}", WORLD);
			LevelSettings settings = new LevelSettings("GrannyCraft", GameType.SURVIVAL, LevelSettings.DifficultySettings.DEFAULT, true, WorldDataConfiguration.DEFAULT);
			minecraft.createWorldOpenFlows().createFreshLevel(WORLD, settings, new WorldOptions(0L, false, false), GrannyCraftClient::voidWorld, minecraft.gui.screen());
		}
	}

	/** A flat world with a single layer of air: nothing but what gets built (Granny's house arrives as ghost blocks). */
	private static WorldDimensions voidWorld(final HolderLookup.Provider registries) {
		FlatLevelGeneratorSettings flat = new FlatLevelGeneratorSettings(
			Optional.of(HolderSet.direct()), registries.lookupOrThrow(Registries.BIOME).getOrThrow(Biomes.PLAINS), List.of()
		);
		flat.getLayersInfo().add(new FlatLayerInfo(1, Blocks.AIR));
		flat.updateLayers();
		return WorldPresets.createNormalWorldDimensions(registries).replaceOverworldGenerator(registries, new FlatLevelSource(flat));
	}
}
