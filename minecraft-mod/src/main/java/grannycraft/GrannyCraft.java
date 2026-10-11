package grannycraft;

import java.util.function.Consumer;
import net.fabricmc.api.ModInitializer;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.fabric.api.event.player.AttackBlockCallback;
import net.fabricmc.fabric.api.event.player.PlayerBlockBreakEvents;
import net.fabricmc.fabric.api.object.builder.v1.entity.FabricDefaultAttributeRegistry;
import net.minecraft.core.Registry;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.core.registries.Registries;
import net.minecraft.resources.Identifier;
import net.minecraft.resources.ResourceKey;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.MobCategory;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.state.BlockBehaviour;
import net.minecraft.world.level.material.PushReaction;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/**
 * GrannyCraft: Minecraft runs alongside Granny Legacy. Granny's camera drives Minecraft's, Granny's house becomes
 * invisible collision ("ghost" blocks), Minecraft's attacks, arrows and explosions are acted out on Granny's own
 * characters by the Granny plugin, and Minecraft's picture goes back to Granny to be composited. Built on universal-modder's minecraft-gta5-passthrough (MIT).
 */
public class GrannyCraft implements ModInitializer {
	public static final String ID = "grannycraft";
	public static final Logger LOG = LoggerFactory.getLogger(ID);
	/** True while Granny is driving the camera. The integrated server shares this JVM, so both sides read it. */
	public static volatile boolean active;
	/** Where events for Granny go (JSON); the client's HostLink sets it. */
	public static volatile Consumer<String> events = message -> {};
	public static Block GHOST;
	/** Where Granny's player stands (Minecraft coordinates, feet), set by the client every tick; null when unknown. */
	public static volatile double[] playerFeet;
	/**
	 * The creative inventory is open over Granny (R): the player takes items like in creative mode, on both sides
	 * (client and integrated server share this JVM), while staying in survival for everything else.
	 */
	public static volatile boolean creativeMenu;

	@Override
	public void onInitialize() {
		ResourceKey<Block> key = ResourceKey.create(Registries.BLOCK, Identifier.fromNamespaceAndPath(ID, "ghost"));
		GHOST = Registry.register(BuiltInRegistries.BLOCK, key, new GhostBlock(BlockBehaviour.Properties.of()
			.setId(key)
			.strength(-1.0F, 3600000.0F)
			.noLootTable()
			.noOcclusion()
			.dynamicShape()
			.pushReaction(PushReaction.IMMOVEABLE)
			.isSuffocating((s, l, p) -> false)
			.isViewBlocking((s, l, p, a) -> false)));

		// Granny and Grandpa as Minecraft sees them: what hostile mobs chase and hit (StandIn)
		ResourceKey<EntityType<?>> standIn = ResourceKey.create(Registries.ENTITY_TYPE, Identifier.fromNamespaceAndPath(ID, "stand_in"));
		StandIn.TYPE = Registry.register(BuiltInRegistries.ENTITY_TYPE, standIn, EntityType.Builder.<StandIn>of(StandIn::new, MobCategory.MISC)
			.sized(0.6F, 1.8F)
			.noSave()
			.noSummon()
			.fireImmune()
			.noLootTable()
			.clientTrackingRange(10)
			.build(standIn));
		FabricDefaultAttributeRegistry.register(StandIn.TYPE, StandIn.attributes());

		// Granny's walls can't be broken, unless the (experimental) breakable house is on
		PlayerBlockBreakEvents.BEFORE.register((level, player, pos, state, be) -> !state.is(GHOST) || GhostBlock.BREAKABLE);
		AttackBlockCallback.EVENT.register((player, level, hand, pos, dir) ->
			level.getBlockState(pos).is(GHOST) && !GhostBlock.BREAKABLE ? InteractionResult.FAIL : InteractionResult.PASS);

		ServerLifecycleEvents.SERVER_STARTED.register(WorldBridge::attach);
		ServerLifecycleEvents.SERVER_STOPPING.register(server -> {
			NetherZone.stop(server);
			WorldBridge.detach();
		});
		ServerTickEvents.END_SERVER_TICK.register(WorldBridge::tick);
		LOG.info("grannycraft loaded");
	}
}
