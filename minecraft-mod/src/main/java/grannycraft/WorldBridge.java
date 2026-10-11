package grannycraft;

import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import net.minecraft.core.BlockPos;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.Vec3;
import grannycraft.mixin.ArrowAccessor;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.monster.Enemy;
import net.minecraft.world.phys.AABB;

/** Server side: Granny's geometry as ghost blocks, Minecraft block changes and explosions back to Granny, commands. */
public final class WorldBridge {
	private static volatile MinecraftServer server;
	/** Block changes this tick (server thread only): true = now solid, false = gone. Sent at the end of the tick. */
	private static final Map<BlockPos, Boolean> changes = new LinkedHashMap<>();
	/** While placing or removing Granny's own geometry: those changes aren't news to Granny. */
	private static boolean placingGhosts;

	private WorldBridge() {
	}

	static void attach(final MinecraftServer s) {
		server = s;
	}

	static void detach() {
		server = null;
	}

	public static MinecraftServer server() {
		return server;
	}

	/** {x, y, z, maskLow, maskHigh, ...}: mask 0 removes the cell. Only air (or an old ghost) is replaced. */
	public static void ghosts(final long[] cells) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			BlockState ghost = GrannyCraft.GHOST.defaultBlockState();
			BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
			placingGhosts = true;
			int set = 0, cleared = 0;
			for (int i = 0; i + 4 < cells.length; i += 5) {
				pos.set((int) cells[i], (int) cells[i + 1], (int) cells[i + 2]);
				if (!level.isInWorldBounds(pos) || GhostBlock.BROKEN.contains(pos.asLong())) {
					continue; // broken out of the house: it stays open
				}

				long mask = (cells[i + 3] & 0xFFFFFFFFL) | (cells[i + 4] << 32);
				BlockState state = level.getBlockState(pos);
				if (mask != 0L) {
					Long was = GhostBlock.MASKS.put(pos.asLong(), mask);
					if (was != null && was != mask && state.is(GrannyCraft.GHOST)) {
						level.getPathTypeCache().invalidate(pos); // same block, new shape (a door moved): mobs path anew
					}
					if (!state.isAir() && !state.is(GrannyCraft.GHOST)) {
						GhostBlock.REPLACED.add(pos.asLong()); // a Minecraft block is there (built in a hole): house again when it goes
					}

					if (state.isAir()) {
						level.setBlock(pos, ghost, Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE);
						set++;
					}
				} else {
					GhostBlock.MASKS.remove(pos.asLong());
					if (state.is(GrannyCraft.GHOST)) {
						level.setBlock(pos, Blocks.AIR.defaultBlockState(), Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE);
						cleared++;
					} else if (NetherZone.owns(pos)) {
						GhostBlock.REPLACED.remove(pos.asLong());
						NetherZone.houseGone(level, pos.immutable()); // the Nether's skin on a door that opened
						cleared++;
					}
				}
			}

			placingGhosts = false;
			GrannyCraft.LOG.info("ghost cells: {} placed, {} cleared ({} known)", set, cleared, GhostBlock.MASKS.size());
		});
	}

	/** Run a command as the server (op). */
	public static void command(final String command) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> s.getCommands().performPrefixedCommand(s.createCommandSourceStack().withSuppressedOutput(), command));
	}

	/** Block changes that aren't news to Granny (the Nether zone's skin on her house): not reported, no carving. */
	static void quietly(final Runnable change) {
		boolean was = placingGhosts;
		placingGhosts = true;
		try {
			change.run();
		} finally {
			placingGhosts = was;
		}
	}

	static void tick(final MinecraftServer s) {
		flush();
		if (GrannyCraft.active) {
			reportArrows(s.overworld());
			pickUp(s);
			if (s.getTickCount() % 2 == 0) {
				reportMobs(s.overworld());
			}
		}

		NetherZone.tick(s);
	}

	/**
	 * The server's copy of the player stands where Granny's player stands, and picks up the items it walks over
	 * (Minecraft's own pickup: same stack first, then the first free slot). Without this the server's player could lag
	 * behind and items on the floor were never picked up.
	 */
	private static void pickUp(final MinecraftServer s) {
		double[] f = GrannyCraft.playerFeet;
		if (f == null || s.getPlayerList().getPlayers().isEmpty()) {
			return;
		}

		ServerPlayer p = s.getPlayerList().getPlayers().get(0);
		if (p.isDeadOrDying() || p.isSpectator()) {
			return;
		}

		p.setPos(f[0], f[1], f[2]);
		for (ItemEntity item : p.level().getEntitiesOfClass(ItemEntity.class, p.getBoundingBox().inflate(1.0, 0.5, 1.0))) {
			if (!item.isRemoved() && !item.hasPickUpDelay()) {
				item.playerTouch(p);
			}
		}
	}

	/** Arrows Granny already took (they ended in Granny or Grandpa). */
	private static final String HIT_TAG = "grannycraft_hit";

	/**
	 * Steve's arrows in flight, every tick: {"t":"proj","p":[[id,x,y,z,vx,vy,vz,damage],...]}. Granny traces them
	 * through her own world against Granny, Grandpa and the others; damage is Minecraft's (speed x base damage).
	 */
	private static void reportArrows(final ServerLevel level) {
		StringBuilder b = null;
		for (Entity e : level.getAllEntities()) {
			if (!(e instanceof AbstractArrow arrow) || !(arrow.getOwner() instanceof Player) || arrow.entityTags().contains(HIT_TAG)) {
				continue;
			}

			ArrowAccessor a = (ArrowAccessor) arrow;
			Vec3 v = arrow.getDeltaMovement();
			if (a.grannycraft$inGround() || v.lengthSqr() < 1.0E-4) {
				continue;
			}

			double damage = Math.ceil(Math.clamp(v.length() * a.grannycraft$baseDamage(), 0.0, Integer.MAX_VALUE));
			if (arrow.isCritArrow()) {
				damage += damage / 2.0 + 1.0;
			}

			b = b == null ? new StringBuilder("{\"t\":\"proj\",\"p\":[") : b.append(',');
			b.append(String.format(Locale.ROOT, "[%d,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.1f]", e.getId(), e.getX(), e.getY(), e.getZ(), v.x, v.y, v.z, damage));
		}

		if (b != null) {
			GrannyCraft.events.accept(b.append("]}").toString());
		}
	}

	/** Granny traced an arrow into one of hers: it ends there (gone, like an arrow in a mob). */
	public static void arrowHit(final int id, final double[] at) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			Entity e = s.overworld().getEntity(id);
			if (!(e instanceof AbstractArrow)) {
				return;
			}

			e.addTag(HIT_TAG);
			if (at == null) {
				e.discard(); // into Granny or Grandpa: gone, like an arrow into a mob
			} else {
				// against one of Granny's objects (a vase, a box...): it stops there and drops, like hitting anything
				Vec3 v = e.getDeltaMovement();
				e.setPos(at[0] - v.x * 0.05, at[1] - v.y * 0.05, at[2] - v.z * 0.05);
				e.setDeltaMovement(v.scale(-0.05));
			}
		});
	}

	/** Server thread, from Level.setBlock: remember the change; flushed once per tick. */
	public static void onBlockChanged(final ServerLevel level, final BlockPos pos, final BlockState state) {
		if (placingGhosts || level != level.getServer().overworld() || state.is(GrannyCraft.GHOST)) {
			return;
		}

		long key = pos.asLong();
		boolean houseCell = GhostBlock.MASKS.containsKey(key);
		if (state.isAir() && houseCell && !GhostBlock.REPLACED.contains(key)) {
			// a piece of Granny's house broken (pickaxe, hand, TNT): it stays open, and Granny cuts her geometry there
			GhostBlock.MASKS.remove(key);
			GhostBlock.BROKEN.add(key);
			carved.add(pos.immutable());
			return;
		}

		if (!state.isAir() && houseCell) {
			GhostBlock.REPLACED.add(key); // a Minecraft block took a piece of house's place
		}

		boolean solid = !state.isAir() && !state.getCollisionShape(level, pos).isEmpty();
		changes.put(pos.immutable(), solid);
		// a Minecraft block removed where Granny's geometry is: the ghost comes back
		if (state.isAir() && houseCell) {
			GhostBlock.REPLACED.remove(key);
			BlockPos p = pos.immutable();
			level.getServer().execute(() -> {
				if (level.getBlockState(p).isAir()) {
					placingGhosts = true;
					level.setBlock(p, GrannyCraft.GHOST.defaultBlockState(), Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE);
					placingGhosts = false;
				}
			});
		}
	}

	/** Cells of Granny's house broken this tick (server thread only). */
	private static final java.util.List<BlockPos> carved = new java.util.ArrayList<>();

	/** End of each server tick: {"t":"carve","c":[x,y,z,...]} for the pieces of house broken. */
	private static void flushCarved() {
		if (carved.isEmpty()) {
			return;
		}

		StringBuilder b = new StringBuilder("{\"t\":\"carve\",\"c\":[");
		for (int i = 0; i < carved.size(); i++) {
			BlockPos p = carved.get(i);
			b.append(i == 0 ? "" : ",").append(p.getX()).append(',').append(p.getY()).append(',').append(p.getZ());
		}

		carved.clear();
		GrannyCraft.events.accept(b.append("]}").toString());
	}

	/** End of each server tick: {"t":"blocks","set":[x,y,z,...],"clear":[x,y,z,...]}. */
	static void flush() {
		flushCarved();
		if (changes.isEmpty()) {
			return;
		}

		StringBuilder set = new StringBuilder();
		StringBuilder clear = new StringBuilder();
		for (Map.Entry<BlockPos, Boolean> e : changes.entrySet()) {
			StringBuilder b = e.getValue() ? set : clear;
			BlockPos p = e.getKey();
			b.append(b.isEmpty() ? "" : ",").append(p.getX()).append(',').append(p.getY()).append(',').append(p.getZ());
		}

		changes.clear();
		GrannyCraft.events.accept("{\"t\":\"blocks\",\"set\":[" + set + "],\"clear\":[" + clear + "]}");
	}

	/**
	 * A new Granny game: everything built in the house goes (Granny's copies went with her old scene), and so do
	 * arrows, primed TNT and dropped items. Granny's geometry (ghost blocks) stays.
	 */
	public static void newGame() {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			// the house spans about 50 x 25 x 30 blocks around the origin (Granny's coordinates are mapped there)
			int minX = -48, minY = 20, minZ = -48, maxX = 48, maxY = 100, maxZ = 48;

			int removed = 0;
			NetherZone.reset(); // its blocks and creatures go with everything else below
			GhostBlock.MASKS.clear();
			GhostBlock.BROKEN.clear();
			GhostBlock.REPLACED.clear();
			if (minX <= maxX) {
				BlockPos.MutableBlockPos p = new BlockPos.MutableBlockPos();
				placingGhosts = true;
				for (int x = minX - 8; x <= maxX + 8; x++) {
					for (int y = Math.max(level.getMinY(), minY - 8); y <= Math.min(level.getMaxY(), maxY + 8); y++) {
						for (int z = minZ - 8; z <= maxZ + 8; z++) {
							p.set(x, y, z);
							BlockState state = level.getBlockState(p);
							// built blocks, and Granny's old geometry too (it is sent again, maybe at another scale)
							if (!state.isAir()) {
								level.setBlock(p, Blocks.AIR.defaultBlockState(), Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE);
								removed++;
							}
						}
					}
				}

				placingGhosts = false;
			}

			s.getCommands().performPrefixedCommand(s.createCommandSourceStack().withSuppressedOutput(), "kill @e[type=!player]");
			GrannyCraft.LOG.info("new Granny game: {} built blocks removed", removed);
		});
	}

	/** Every solid Minecraft block near the player (Granny reconnected and lost her copies). */
	public static void sync(final int radius) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			if (s.getPlayerList().getPlayers().isEmpty()) {
				return;
			}

			BlockPos c = s.getPlayerList().getPlayers().get(0).blockPosition();
			BlockPos.MutableBlockPos p = new BlockPos.MutableBlockPos();
			for (int x = -radius; x <= radius; x++) {
				for (int z = -radius; z <= radius; z++) {
					for (int y = -16; y <= 24; y++) {
						p.set(c.getX() + x, c.getY() + y, c.getZ() + z);
						if (!level.isInWorldBounds(p) || !level.isLoaded(p)) {
							continue;
						}

						BlockState state = level.getBlockState(p);
						if (!state.isAir() && !state.is(GrannyCraft.GHOST) && !state.getCollisionShape(level, p).isEmpty()) {
							changes.put(p.immutable(), true);
						}
					}
				}
			}

			flush();
		});
	}

	/** `source`: what exploded, e.g. "tnt", "creeper". */
	public static void onExplosion(final Vec3 center, final float radius, final String source) {
		if (GrannyCraft.active) {
			GrannyCraft.events.accept(String.format(Locale.ROOT, "{\"t\":\"explosion\",\"pos\":[%.3f,%.3f,%.3f],\"r\":%.2f,\"src\":\"%s\"}",
				center.x, center.y, center.z, radius, source));
		}
	}

	/** Granny and Grandpa in Minecraft, by Granny's id for them (server thread only). */
	private static final Map<Integer, StandIn> standIns = new java.util.HashMap<>();

	/**
	 * {"t":"standins","s":[[id,"granny",x,y,z,width,height,yaw],...]}: where Granny and Grandpa stand (only while they
	 * are up and about). Their stand-ins follow; one no longer sent goes (StandIn.tick also drops it on its own).
	 */
	public static void standIns(final com.google.gson.JsonArray list) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			java.util.Set<Integer> sent = new java.util.HashSet<>();
			for (com.google.gson.JsonElement el : list) {
				com.google.gson.JsonArray a = el.getAsJsonArray();
				int id = a.get(0).getAsInt();
				sent.add(id);
				double x = a.get(2).getAsDouble(), y = a.get(3).getAsDouble(), z = a.get(4).getAsDouble();
				StandIn st = standIns.get(id);
				if (!level.isPositionEntityTicking(BlockPos.containing(x, y, z))) {
					sent.remove(id); // not loaded around there (yet): it would be unloaded right away
					continue;
				}

				if (st == null || st.isRemoved()) {
					st = new StandIn(StandIn.TYPE, level);
					st.hostId = id;
					st.kind = a.get(1).getAsString();
					st.resize(a.get(5).getAsFloat(), a.get(6).getAsFloat());
					st.snapTo(x, y, z, a.get(7).getAsFloat(), 0.0F);
					st.seen = st.tickCount;
					level.addFreshEntity(st);
					standIns.put(id, st);
					GrannyCraft.LOG.info("{} stands in Minecraft at {}", st.kind, st.blockPosition());
				} else {
					st.resize(a.get(5).getAsFloat(), a.get(6).getAsFloat());
					st.snapTo(x, y, z, a.get(7).getAsFloat(), 0.0F);
					st.setYHeadRot(a.get(7).getAsFloat());
					st.seen = st.tickCount;
				}
			}

			standIns.entrySet().removeIf(e -> {
				if (sent.contains(e.getKey()) && !e.getValue().isRemoved()) {
					return false;
				}

				e.getValue().discard();
				return true;
			});
		});
	}

	/** Server thread: a mob hit Granny's stand-in. {"t":"hurt","id":Granny's id,"d":damage,"by":mob type,"from":[x,y,z]} */
	static void standInHurt(final StandIn st, final Mob by, final float amount) {
		GrannyCraft.events.accept(String.format(Locale.ROOT, "{\"t\":\"hurt\",\"id\":%d,\"d\":%.2f,\"by\":\"%s\",\"from\":[%.3f,%.3f,%.3f]}",
			st.hostId, amount, BuiltInRegistries.ENTITY_TYPE.getKey(by.getType()).getPath(), by.getX(), by.getY(), by.getZ()));
	}

	private static boolean mobsSent;

	/**
	 * Every other tick, the hostile mobs around the player for Granny and Grandpa to fight:
	 * {"t":"mobs","m":[[id,x,y,z,height,width,"zombie",target],...]}, target = Granny's id of the stand-in it is after (0: none).
	 */
	private static void reportMobs(final ServerLevel level) {
		if (level.getServer().getPlayerList().getPlayers().isEmpty()) {
			return;
		}

		ServerPlayer p = level.getServer().getPlayerList().getPlayers().get(0);
		StringBuilder b = new StringBuilder("{\"t\":\"mobs\",\"m\":[");
		int n = 0;
		for (Mob m : level.getEntitiesOfClass(Mob.class, new AABB(p.blockPosition()).inflate(64.0), m -> m instanceof Enemy && m.isAlive() && !(m instanceof StandIn))) {
			int target = m.getTarget() instanceof StandIn st ? st.hostId : 0;
			b.append(n++ == 0 ? "" : ",").append(String.format(Locale.ROOT, "[%d,%.3f,%.3f,%.3f,%.2f,%.2f,\"%s\",%d]", m.getId(), m.getX(), m.getY(), m.getZ(),
				m.getBbHeight(), m.getBbWidth(), BuiltInRegistries.ENTITY_TYPE.getKey(m.getType()).getPath(), target));
		}

		if (n > 0 || mobsSent) {
			mobsSent = n > 0; // an empty list once, when the last one goes
			GrannyCraft.events.accept(b.append("]}").toString());
		}
	}

	/** {"t":"mobhit","id":mob,"d":damage,"by":Granny's id}: Granny or Grandpa hit a mob (it fights back). */
	public static void mobHit(final int id, final float damage, final int by) {
		MinecraftServer s = server;
		if (s == null) {
			return;
		}

		s.execute(() -> {
			ServerLevel level = s.overworld();
			if (!(level.getEntity(id) instanceof Mob mob) || !mob.isAlive()) {
				return;
			}

			StandIn st = standIns.get(by);
			boolean hit = mob.hurtServer(level, st != null && !st.isRemoved() ? mob.damageSources().mobAttack(st) : mob.damageSources().generic(), damage);
			if (hit && st != null && !st.isRemoved() && mob instanceof Enemy) {
				mob.setTarget(st); // she hit it: it goes for her
			}
		});
	}

}
