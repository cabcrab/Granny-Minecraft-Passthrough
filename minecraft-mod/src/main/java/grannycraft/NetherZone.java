package grannycraft;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Random;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.block.BaseFireBlock;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.NetherWartBlock;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.HitResult;
import net.minecraft.world.phys.Vec3;

/**
 * Steve walks into a lit nether portal: there is no Nether to go to over Granny (Minecraft's own portal travel is off),
 * so the Nether comes to Granny's house instead. Around the player the house's surfaces turn to netherrack, nylium,
 * soul sand, magma and glowstone; roots, fungi, nether wart and fire grow on the floor; netherrack and blackstone
 * spikes rise from it and glowstone hangs from the ceiling; ash and spores fill the air, ghasts moan; and a crowd of
 * nether creatures arrives (piglins, a brute, zombified piglins, hoglins, magma cubes, blazes, wither skeletons, a
 * strider). After {@link #seconds} everything goes back as it was, like the broken pieces of the house grow back.
 *
 * <p>Only cells of Granny's house whose geometry reaches the cell's open faces are swapped: the swapped block then
 * never sticks out of Granny's own surfaces (her floors sit on block boundaries, so floors always qualify). These
 * swaps aren't reported to Granny (her house is already there), and Minecraft treats them like a block built into
 * the house: broken or blown up, the house's ghost comes straight back.
 */
public final class NetherZone {
	/** Mobs of the event: removed when it ends. */
	private static final String TAG = "grannycraft_nether";
	/** How long the Nether stays, and how far it reaches (blocks); the Granny plugin sends its settings. */
	public static volatile int seconds = 30;
	public static volatile int radius = 16;
	private static final int BELOW = 3, ABOVE = 9;
	/** Solid blocks the event stood in the room (spikes, hanging glowstone): Granny gets them as collision too. */
	private static final java.util.Set<BlockPos> FEATURES = new java.util.HashSet<>();
	private static final String[] PARTICLES = {"ash", "white_ash", "crimson_spore", "warped_spore", "soul_fire_flame", "dripping_lava", "flame"};
	private static final String[] SOUNDS = {
		"entity.ghast.ambient", "entity.ghast.scream", "ambient.nether_wastes.mood", "ambient.crimson_forest.mood",
		"ambient.soul_sand_valley.mood", "entity.blaze.ambient", "entity.piglin.angry", "entity.hoglin.ambient"
	};
	private static final Random AMBIENT = new Random();

	/** What was there before each changed cell, and what the event put there (insertion order: house first). */
	private static final Map<BlockPos, BlockState[]> CHANGED = new LinkedHashMap<>();
	private static int ticksLeft = -1;
	/** The player was in a portal last tick: entering again only counts after stepping out. */
	private static boolean inPortal;

	private NetherZone() {
	}

	public static boolean active() {
		return ticksLeft >= 0;
	}

	/** Server thread: did the event change this cell? */
	public static boolean owns(final BlockPos pos) {
		return CHANGED.containsKey(pos);
	}

	/** Server thread: Granny's geometry left a cell the event had swapped (a door opened): it goes, quietly. */
	public static void houseGone(final ServerLevel level, final BlockPos pos) {
		BlockState[] s = CHANGED.remove(pos);
		if (s != null && level.getBlockState(pos) == s[1]) {
			WorldBridge.quietly(() -> level.setBlock(pos, Blocks.AIR.defaultBlockState(), Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE));
		}
	}

	/** Every server tick: the player stepping into a portal starts it, the timer ends it. */
	static void tick(final MinecraftServer s) {
		if (ticksLeft > 0 && --ticksLeft == 0) {
			end(s);
		}

		if (active() && !s.getPlayerList().getPlayers().isEmpty()) {
			ambience(s, s.getPlayerList().getPlayers().get(0));
		}

		if (!GrannyCraft.active || s.getPlayerList().getPlayers().isEmpty()) {
			return;
		}

		ServerPlayer p = s.getPlayerList().getPlayers().get(0);
		boolean now = !p.isSpectator() && touchesPortal(p.level(), p.getBoundingBox());
		if (now && !inPortal && !active()) {
			begin(s, p);
		}

		inPortal = now;
	}

	private static boolean touchesPortal(final ServerLevel level, final AABB box) {
		BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
		for (int x = (int) Math.floor(box.minX); x <= (int) Math.floor(box.maxX); x++) {
			for (int y = (int) Math.floor(box.minY); y <= (int) Math.floor(box.maxY); y++) {
				for (int z = (int) Math.floor(box.minZ); z <= (int) Math.floor(box.maxZ); z++) {
					if (level.getBlockState(pos.set(x, y, z)).is(Blocks.NETHER_PORTAL)) {
						return true;
					}
				}
			}
		}

		return false;
	}

	private static void begin(final MinecraftServer s, final ServerPlayer player) {
		ServerLevel level = s.overworld();
		BlockPos c = player.blockPosition();
		Random rnd = new Random(level.getGameTime());
		int r = Math.max(4, radius);
		List<BlockPos> floors = new ArrayList<>();
		List<BlockPos> ceilings = new ArrayList<>();
		BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();

		WorldBridge.quietly(() -> {
			// 1. the house's surfaces
			for (int x = -r; x <= r; x++) {
				for (int z = -r; z <= r; z++) {
					if (x * x + z * z > r * r) {
						continue;
					}

					for (int y = -BELOW; y <= ABOVE; y++) {
						pos.set(c.getX() + x, c.getY() + y, c.getZ() + z);
						BlockState state = level.getBlockState(pos);
						Long mask = GhostBlock.MASKS.get(pos.asLong());
						if (!state.is(GrannyCraft.GHOST) || mask == null) {
							continue;
						}

						int open = openFaces(level, pos, mask);
						if (open <= 0) {
							continue;
						}

						boolean isFloor = (open & (1 << Direction.UP.ordinal())) != 0 && faceFill(mask, Direction.UP) >= 8;
						BlockState to = isFloor ? floor(rnd) : (open & (1 << Direction.DOWN.ordinal())) != 0 ? ceiling(rnd) : wall(rnd);
						BlockPos at = pos.immutable();
						swap(level, at, state, to);
						GhostBlock.REPLACED.add(at.asLong()); // broken or blown up: the house's ghost comes back
						if (isFloor) {
							floors.add(at);
						} else if ((open & (1 << Direction.DOWN.ordinal())) != 0) {
							ceilings.add(at);
						}
					}
				}
			}

			// 2. what grows on the floor (nothing solid: Granny's player and Granny walk through it, as Steve does)
			for (BlockPos f : floors) {
				BlockPos up = f.above();
				if (!level.getBlockState(up).isAir() || horizontalDistSq(up, player) < 2.5 * 2.5) {
					continue;
				}

				BlockState below = level.getBlockState(f);
				BlockState plant = null;
				double roll = rnd.nextDouble();
				if (below.is(Blocks.CRIMSON_NYLIUM)) {
					plant = roll < 0.5 ? Blocks.CRIMSON_ROOTS.defaultBlockState() : roll < 0.7 ? Blocks.CRIMSON_FUNGUS.defaultBlockState() : null;
				} else if (below.is(Blocks.WARPED_NYLIUM)) {
					plant = roll < 0.35 ? Blocks.WARPED_ROOTS.defaultBlockState() : roll < 0.55 ? Blocks.NETHER_SPROUTS.defaultBlockState()
						: roll < 0.7 ? Blocks.WARPED_FUNGUS.defaultBlockState() : null;
				} else if (below.is(Blocks.SOUL_SAND)) {
					plant = roll < 0.45 ? Blocks.NETHER_WART.defaultBlockState().setValue(NetherWartBlock.AGE, NetherWartBlock.MAX_AGE)
						: roll < 0.6 ? Blocks.SOUL_FIRE.defaultBlockState() : null;
				} else if (below.is(Blocks.NETHERRACK) && roll < 0.15) {
					plant = BaseFireBlock.getState(level, up);
				}

				if (plant != null) {
					swap(level, up, Blocks.AIR.defaultBlockState(), plant);
				}
			}
		});

		// 3. the Nether's rocks: spikes rising from the floor, glowstone hanging from the ceiling. Solid, so Granny gets
		// them too (her player bumps into them, Granny walks around them); never right by the player.
		BlockState[] rocks = {
			Blocks.NETHERRACK.defaultBlockState(), Blocks.NETHERRACK.defaultBlockState(), Blocks.BLACKSTONE.defaultBlockState(),
			Blocks.BASALT.defaultBlockState(), Blocks.NETHER_BRICKS.defaultBlockState(), Blocks.MAGMA_BLOCK.defaultBlockState()
		};
		List<BlockPos> shuffled = new ArrayList<>(floors);
		java.util.Collections.shuffle(shuffled, rnd);
		int spikes = 0;
		for (BlockPos f : shuffled) {
			if (spikes >= r) {
				break;
			}

			if (horizontalDistSq(f.above(), player) < 4.0 * 4.0) {
				continue;
			}

			BlockState rock = rocks[rnd.nextInt(rocks.length)];
			int h = 1 + rnd.nextInt(3), placed = 0;
			for (int i = 1; i <= h && level.getBlockState(f.above(i)).isAir(); i++) {
				feature(level, f.above(i), i == h && rnd.nextDouble() < 0.35 ? Blocks.GLOWSTONE.defaultBlockState() : rock);
				placed++;
			}

			if (placed > 0) {
				spikes++;
			}
		}

		java.util.Collections.shuffle(ceilings, rnd);
		int hanging = 0;
		for (BlockPos c2 : ceilings) {
			if (hanging >= r * 2 / 3) {
				break;
			}

			if (horizontalDistSq(c2.below(), player) < 3.0 * 3.0) {
				continue;
			}

			int h = 1 + rnd.nextInt(2), placed = 0;
			for (int i = 1; i <= h && level.getBlockState(c2.below(i)).isAir(); i++) {
				feature(level, c2.below(i), rnd.nextDouble() < 0.6 ? Blocks.GLOWSTONE.defaultBlockState()
					: rnd.nextBoolean() ? Blocks.SHROOMLIGHT.defaultBlockState() : Blocks.NETHERRACK.defaultBlockState());
				placed++;
			}

			if (placed > 0) {
				hanging++;
			}
		}

		// 4. the Nether's creatures, on open floor a little away from the player, in the room he sees (a crowd: spots are
		// shared if few). Open all around, not only above: Granny's walls are often two thin panels with a gap between,
		// floor under it, and a wide mob next to a wall would stand half in it.
		List<BlockPos> spots = new ArrayList<>(), hidden = new ArrayList<>();
		for (BlockPos f : floors) {
			BlockPos feet = f.above();
			if (horizontalDistSq(feet, player) <= 3.5 * 3.5 || !roomy(level, feet)) {
				continue;
			}

			(inSight(level, player, feet) ? spots : hidden).add(feet);
		}

		if (spots.size() < 6) {
			spots.addAll(hidden); // a small room: the next ones too (still with room around them)
		}

		String[] mobs = {
			"zombified_piglin", "zombified_piglin", "zombified_piglin", "zombified_piglin", "zombified_piglin",
			"piglin", "piglin", "piglin", "piglin_brute", "hoglin", "hoglin",
			"magma_cube", "magma_cube", "magma_cube", "blaze", "blaze", "wither_skeleton", "wither_skeleton", "strider"
		};
		int spawned = 0;
		for (String mob : mobs) {
			if (spots.isEmpty()) {
				break;
			}

			// a spot of its own while there are enough, else one already used
			BlockPos at = spots.size() > mobs.length - spawned ? spots.remove(rnd.nextInt(spots.size())) : spots.get(rnd.nextInt(spots.size()));
			String nbt = "PersistenceRequired:1b,Tags:[\"" + TAG + "\"]";
			if (mob.equals("piglin") || mob.equals("piglin_brute") || mob.equals("hoglin")) {
				nbt += ",IsImmuneToZombification:1b"; // they'd turn into zombies in the Overworld
			} else if (mob.equals("magma_cube")) {
				nbt += ",Size:" + rnd.nextInt(2); // a big one wouldn't fit between Granny's furniture
			}

			s.getCommands().performPrefixedCommand(s.createCommandSourceStack().withSuppressedOutput(), String.format(Locale.ROOT,
				"summon minecraft:%s %.2f %d %.2f {%s}", mob, at.getX() + 0.5, at.getY(), at.getZ() + 0.5, nbt));
			spawned++;
		}

		ticksLeft = Math.max(1, seconds) * 20;
		command(s, String.format(Locale.ROOT, "playsound minecraft:block.portal.travel ambient @a %.2f %.2f %.2f 0.5 1", player.getX(), player.getY(), player.getZ()));
		GrannyCraft.LOG.info("nether zone: {} cells changed ({} spikes, {} hanging), {} creatures, for {} s", CHANGED.size(), spikes, hanging, spawned, seconds);
		GrannyCraft.events.accept("{\"t\":\"nether\",\"on\":true,\"secs\":" + Math.max(1, seconds) + "}");
	}

	/**
	 * Bit d of the result: face d of the cell is open (air beside it). -1: an open face isn't Granny's surface (her
	 * geometry is inside the cell there: a whole block would stick out of it). A floor (its top on the cell's top) or a
	 * ceiling (its underside on the cell's bottom) counts even when the other side is open too: the block only reaches
	 * into the space under the floor, or above the ceiling, which nobody looks at from this room.
	 */
	private static int openFaces(final ServerLevel level, final BlockPos pos, final long mask) {
		int open = 0;
		for (Direction d : Direction.values()) {
			if (level.getBlockState(pos.relative(d)).isAir()) {
				open |= 1 << d.ordinal();
			}
		}

		int up = 1 << Direction.UP.ordinal(), down = 1 << Direction.DOWN.ordinal();
		boolean floor = (open & up) != 0 && faceFill(mask, Direction.UP) >= 8;
		boolean ceiling = (open & down) != 0 && faceFill(mask, Direction.DOWN) >= 8;
		for (Direction d : Direction.values()) {
			if ((open & (1 << d.ordinal())) == 0 || faceFill(mask, d) >= 8) {
				continue;
			}

			if (d == Direction.DOWN && floor || d == Direction.UP && ceiling) {
				continue;
			}

			return -1;
		}

		return open;
	}

	/** Filled sub-voxels (of 16) in the layer of a cell touching face d. Bit i + 4j + 16k = sub-voxel (i, j, k). */
	private static int faceFill(final long mask, final Direction d) {
		int n = 0;
		for (int a = 0; a < 4; a++) {
			for (int b = 0; b < 4; b++) {
				int i, j, k;
				switch (d) {
					case UP -> { i = a; j = 3; k = b; }
					case DOWN -> { i = a; j = 0; k = b; }
					case EAST -> { i = 3; j = a; k = b; }
					case WEST -> { i = 0; j = a; k = b; }
					case SOUTH -> { i = a; j = b; k = 3; }
					default -> { i = a; j = b; k = 0; }
				}

				if ((mask >>> (i + 4 * j + 16 * k) & 1L) != 0L) {
					n++;
				}
			}
		}

		return n;
	}

	private static BlockState floor(final Random rnd) {
		double r = rnd.nextDouble();
		return (r < 0.50 ? Blocks.NETHERRACK : r < 0.66 ? Blocks.CRIMSON_NYLIUM : r < 0.76 ? Blocks.WARPED_NYLIUM
			: r < 0.88 ? Blocks.SOUL_SAND : r < 0.93 ? Blocks.MAGMA_BLOCK : r < 0.97 ? Blocks.SOUL_SOIL : Blocks.BLACKSTONE).defaultBlockState();
	}

	private static BlockState ceiling(final Random rnd) {
		double r = rnd.nextDouble();
		return (r < 0.30 ? Blocks.GLOWSTONE : r < 0.36 ? Blocks.SHROOMLIGHT : r < 0.46 ? Blocks.NETHER_WART_BLOCK : Blocks.NETHERRACK).defaultBlockState();
	}

	private static BlockState wall(final Random rnd) {
		double r = rnd.nextDouble();
		return (r < 0.50 ? Blocks.NETHERRACK : r < 0.65 ? Blocks.NETHER_BRICKS : r < 0.78 ? Blocks.BLACKSTONE : r < 0.86 ? Blocks.BASALT
			: r < 0.93 ? Blocks.NETHER_QUARTZ_ORE : Blocks.NETHER_GOLD_ORE).defaultBlockState();
	}

	private static double horizontalDistSq(final BlockPos p, final ServerPlayer player) {
		double dx = p.getX() + 0.5 - player.getX(), dz = p.getZ() + 0.5 - player.getZ();
		return dx * dx + dz * dz;
	}

	/** Nothing in the way in a 3 x 3 column, 3 blocks high, standing on the floor at `feet`. */
	private static boolean roomy(final ServerLevel level, final BlockPos feet) {
		BlockPos.MutableBlockPos p = new BlockPos.MutableBlockPos();
		for (int dx = -1; dx <= 1; dx++) {
			for (int dz = -1; dz <= 1; dz++) {
				for (int dy = 0; dy <= 2; dy++) {
					p.set(feet.getX() + dx, feet.getY() + dy, feet.getZ() + dz);
					if (!level.getBlockState(p).getCollisionShape(level, p).isEmpty()) {
						return false;
					}
				}
			}
		}

		return true;
	}

	/** The player sees the spot (nothing of the house or a solid block between his eyes and it). */
	private static boolean inSight(final ServerLevel level, final ServerPlayer player, final BlockPos feet) {
		Vec3 to = Vec3.atCenterOf(feet.above());
		HitResult hit = level.clip(new ClipContext(player.getEyePosition(), to, ClipContext.Block.COLLIDER, ClipContext.Fluid.NONE, player));
		return hit.getType() == HitResult.Type.MISS || hit.getLocation().distanceToSqr(to) < 0.25;
	}

	/** Ash, spores and embers in the air around the player all the time; now and then a ghast, a blaze, a piglin. */
	private static void ambience(final MinecraftServer s, final ServerPlayer p) {
		if (ticksLeft % 10 == 0) {
			for (int i = 0; i < 2; i++) {
				String particle = PARTICLES[AMBIENT.nextInt(PARTICLES.length)];
				int count = particle.equals("ash") || particle.equals("white_ash") ? 80 : particle.contains("spore") ? 50 : 12;
				command(s, String.format(Locale.ROOT, "particle minecraft:%s %.2f %.2f %.2f 7 2.5 7 0.02 %d normal", particle, p.getX(), p.getY() + 1.5, p.getZ(), count));
			}
		}

		if (ticksLeft % 120 == 60) {
			String sound = SOUNDS[AMBIENT.nextInt(SOUNDS.length)];
			double a = AMBIENT.nextDouble() * Math.PI * 2.0;
			command(s, String.format(Locale.ROOT, "playsound minecraft:%s hostile @a %.2f %.2f %.2f 0.8 %.2f", sound,
				p.getX() + Math.cos(a) * 6.0, p.getY() + 1.0, p.getZ() + Math.sin(a) * 6.0, 0.8 + AMBIENT.nextDouble() * 0.3));
		}
	}

	private static void command(final MinecraftServer s, final String command) {
		s.getCommands().performPrefixedCommand(s.createCommandSourceStack().withSuppressedOutput(), command);
	}

	/** A solid block stood in the room, reported to Granny like a block Steve placed (she gets its collision). */
	private static void feature(final ServerLevel level, final BlockPos pos, final BlockState state) {
		if (level.setBlock(pos, state, Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE)) {
			CHANGED.put(pos, new BlockState[] {Blocks.AIR.defaultBlockState(), state});
			FEATURES.add(pos);
		}
	}

	/** Called inside {@link WorldBridge#quietly}. */
	private static void swap(final ServerLevel level, final BlockPos pos, final BlockState from, final BlockState to) {
		if (level.setBlock(pos, to, Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE)) {
			CHANGED.put(pos, new BlockState[] {from, to});
		}
	}

	/** Everything back as it was: plants first (they'd pop off the floor), then the house, then the creatures go. */
	private static void end(final MinecraftServer s) {
		ticksLeft = -1;
		ServerLevel level = s.overworld();
		List<Map.Entry<BlockPos, BlockState[]>> cells = new ArrayList<>(CHANGED.entrySet());
		CHANGED.clear();
		int restored = 0;
		for (int pass = 0; pass < 2; pass++) {
			for (Map.Entry<BlockPos, BlockState[]> e : cells) {
				BlockPos pos = e.getKey();
				BlockState before = e.getValue()[0], placed = e.getValue()[1];
				boolean house = before.is(GrannyCraft.GHOST);
				if (house != (pass == 1)) {
					continue;
				}

				if (house) {
					GhostBlock.REPLACED.remove(pos.asLong());
				}

				// changed meanwhile (broken, blown up, built over): that change stays
				if (level.getBlockState(pos) != placed) {
					continue;
				}

				// Granny's geometry may have left the cell meanwhile (a door): then it is air
				BlockState back = house && !GhostBlock.MASKS.containsKey(pos.asLong()) ? Blocks.AIR.defaultBlockState() : before;
				Runnable put = () -> level.setBlock(pos, back, Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE);
				if (FEATURES.contains(pos)) {
					put.run(); // Granny loses its collision too
				} else {
					WorldBridge.quietly(put);
				}

				restored++;
			}
		}

		FEATURES.clear();
		int gone = removeCreatures(level);
		GrannyCraft.LOG.info("nether zone over: {} cells back, {} creatures gone", restored, gone);
		GrannyCraft.events.accept("{\"t\":\"nether\",\"on\":false}");
	}

	private static int removeCreatures(final ServerLevel level) {
		List<Entity> mobs = new ArrayList<>();
		for (Entity e : level.getAllEntities()) {
			if (e.entityTags().contains(TAG)) {
				mobs.add(e);
			}
		}

		mobs.forEach(Entity::discard);
		return mobs.size();
	}

	/** Minecraft is closing: put the house back now, so the world isn't saved with the Nether in it. */
	static void stop(final MinecraftServer s) {
		if (active()) {
			end(s);
		}
	}

	/** A new Granny game wipes the house area anyway: just forget the event (server thread). */
	static void reset() {
		CHANGED.clear();
		FEATURES.clear();
		ticksLeft = -1;
		inPortal = false;
	}
}
