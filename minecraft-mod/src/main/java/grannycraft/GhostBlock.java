package grannycraft;

import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import net.minecraft.core.BlockPos;
import net.minecraft.world.item.context.BlockPlaceContext;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.RenderShape;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.pathfinder.PathType;
import net.minecraft.world.phys.shapes.CollisionContext;
import net.minecraft.world.phys.shapes.Shapes;
import net.minecraft.world.phys.shapes.VoxelShape;

/**
 * A piece of Granny's house: invisible, unbreakable, and shaped exactly like Granny's geometry in this cell (4x4x4
 * sub-voxels, sent by the Granny plugin). Arrows stick in it, TNT is stopped by it, blocks can be placed against it,
 * and Minecraft's block outline traces Granny's walls when you aim at them.
 */
public class GhostBlock extends Block {
	/** Occupancy of each ghost cell: bit i + 4j + 16k = sub-voxel (i, j, k). Shared by client and integrated server. */
	public static final Map<Long, Long> MASKS = new ConcurrentHashMap<>();
	/** Experimental: Granny's house can be broken like Minecraft blocks (hand and tools, not TNT). Set by the Granny plugin. */
	public static volatile boolean BREAKABLE;
	/** Cells of Granny's house broken this game: they never come back (Granny cut her geometry there). */
	public static final java.util.Set<Long> BROKEN = ConcurrentHashMap.newKeySet();
	/** Cells where a Minecraft block took a ghost's place: when that block goes, the ghost comes back. */
	public static final java.util.Set<Long> REPLACED = ConcurrentHashMap.newKeySet();
	private static final Map<Long, VoxelShape> SHAPES = new ConcurrentHashMap<>();

	public GhostBlock(final Properties properties) {
		super(properties);
	}

	public static VoxelShape shape(final BlockPos pos) {
		Long mask = MASKS.get(pos.asLong());
		if (mask == null || mask == 0L) {
			return Shapes.empty();
		}

		return SHAPES.computeIfAbsent(mask, GhostBlock::build);
	}

	/**
	 * How mobs' pathfinding sees this cell (WalkNodeEvaluatorMixin). Minecraft judges a block by its shape at the world
	 * origin, which for Granny's house is nothing: every floor and wall looked like air, so mobs found no ground to walk
	 * on and stood still. Here from the cell's own shape: blocked when something is in the middle of the cell (where a
	 * mob's body goes) or a layer spans it from side to side (a floor, a thin wall); open when it only has bits along its
	 * edges (a door frame, the side of a piece of furniture), so doorways stay passable.
	 */
	public static PathType pathType(final BlockPos pos) {
		Long boxed = MASKS.get(pos.asLong());
		long mask = boxed == null ? 0L : boxed;
		if (mask == 0L) {
			return PathType.OPEN;
		}

		for (int j = 0; j < 4; j++) {
			// the middle 2 x 2 sub-voxels of this layer (i, k = 1..2)
			if ((mask >>> (1 + 4 * j + 16) & 3L) != 0L || (mask >>> (1 + 4 * j + 32) & 3L) != 0L) {
				return PathType.BLOCKED;
			}

			for (int n = 0; n < 4; n++) {
				if ((mask >>> (4 * j + 16 * n) & 0xFL) == 0xFL) {
					return PathType.BLOCKED; // a full row along x
				}

				boolean alongZ = true;
				for (int k = 0; k < 4 && alongZ; k++) {
					alongZ = (mask >>> (n + 4 * j + 16 * k) & 1L) != 0L;
				}

				if (alongZ) {
					return PathType.BLOCKED;
				}
			}
		}

		return PathType.OPEN;
	}

	/** Greedy along x: runs of filled sub-voxels become one box each. */
	private static VoxelShape build(final long mask) {
		if (mask == -1L) {
			return Shapes.block();
		}

		VoxelShape shape = Shapes.empty();
		for (int k = 0; k < 4; k++) {
			for (int j = 0; j < 4; j++) {
				int i = 0;
				while (i < 4) {
					if ((mask >>> (i + 4 * j + 16 * k) & 1L) == 0L) {
						i++;
						continue;
					}

					int start = i;
					while (i < 4 && (mask >>> (i + 4 * j + 16 * k) & 1L) != 0L) {
						i++;
					}

					shape = Shapes.or(shape, Shapes.box(start * 0.25, j * 0.25, k * 0.25, i * 0.25, (j + 1) * 0.25, (k + 1) * 0.25));
				}
			}
		}

		return shape.optimize();
	}

	@Override
	protected VoxelShape getShape(final BlockState state, final BlockGetter level, final BlockPos pos, final CollisionContext context) {
		return shape(pos);
	}

	@Override
	protected VoxelShape getCollisionShape(final BlockState state, final BlockGetter level, final BlockPos pos, final CollisionContext context) {
		return shape(pos);
	}

	@Override
	protected VoxelShape getVisualShape(final BlockState state, final BlockGetter level, final BlockPos pos, final CollisionContext context) {
		return Shapes.empty();
	}

	@Override
	protected RenderShape getRenderShape(final BlockState state) {
		return RenderShape.INVISIBLE;
	}

	@Override
	protected boolean propagatesSkylightDown(final BlockState state) {
		return true;
	}

	@Override
	protected float getShadeBrightness(final BlockState state, final BlockGetter level, final BlockPos pos) {
		return 1.0F;
	}

	/** Unbreakable, unless the house is breakable: then like wood planks (hardness 2), any tool. */
	@Override
	protected float getDestroyProgress(final BlockState state, final net.minecraft.world.entity.player.Player player, final BlockGetter level, final BlockPos pos) {
		if (!BREAKABLE) {
			return 0.0F;
		}

		// Granny's house is wood and plaster: an axe or a pickaxe both work (the best of the two), the hand is slow.
		// Computed here rather than with Player.getDestroySpeed: Steve is moved by Granny and never "on the ground" for
		// Minecraft, which would make mining 5 times slower.
		var held = player.getMainHandItem();
		float speed = Math.max(held.getDestroySpeed(net.minecraft.world.level.block.Blocks.OAK_PLANKS.defaultBlockState()),
			held.getDestroySpeed(net.minecraft.world.level.block.Blocks.STONE.defaultBlockState()));
		if (speed > 1.0F) {
			speed += (float) player.getAttributeValue(net.minecraft.world.entity.ai.attributes.Attributes.MINING_EFFICIENCY);
		}

		speed *= (float) player.getAttributeValue(net.minecraft.world.entity.ai.attributes.Attributes.BLOCK_BREAK_SPEED);
		return speed / 2.0F / 30.0F;
	}

	/** TNT never breaks Granny's house (cutting many cells at once is too heavy): only hands and tools do. */
	@Override
	public float getExplosionResistance() {
		return 3600000.0F;
	}

	@Override
	protected boolean canBeReplaced(final BlockState state, final BlockPlaceContext context) {
		// a cell that is only a little Granny's takes a block (it goes right there): Granny's collision comes back when
		// the block is broken (WorldBridge). Floors: only when the floor fills the bottom quarter of the cell at most (the
		// block then sinks at most 1/4 into it); walls standing in the cell: when they fill half of it at most.
		Long boxed = MASKS.get(context.getClickedPos().asLong());
		if (boxed == null) {
			return true;
		}

		long mask = boxed;
		int layers = 0; // which of the 4 horizontal layers (bottom = bit 0) have something
		for (int j = 0; j < 4; j++) {
			for (int k = 0; k < 4; k++) {
				if ((mask >>> (4 * j + 16 * k) & 0xFL) != 0L) {
					layers |= 1 << j;
				}
			}
		}

		if (layers == 0b0001) {
			return true; // floor surface in the bottom quarter
		}

		return layers == 0b1111 && Long.bitCount(mask) <= 32; // a thin wall, top to bottom
	}
}
