package grannycraft;

import net.minecraft.server.level.ServerLevel;
import net.minecraft.sounds.SoundEvent;
import net.minecraft.tags.DamageTypeTags;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntityDimensions;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.Pose;
import net.minecraft.world.entity.ai.attributes.AttributeSupplier;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.Vec3;

/**
 * Granny or Grandpa in Minecraft: an unseen body standing where she stands in Granny's world (the Granny plugin sends
 * where, ~10 times a second), so Minecraft's hostile mobs see her, chase her and hit her. It never moves, falls, dies
 * or gets hurt itself: a mob's hit (melee, arrow, potion...) goes to Granny's side, which acts it out on the real
 * Granny. Explosions and the player's own hits reach Granny their own way, so they are ignored here. Nothing is drawn
 * (a no-op renderer), and it is not invisible either: mobs would barely notice an invisible target.
 */
public class StandIn extends Mob {
	public static EntityType<StandIn> TYPE;
	/** Granny's id for this character (its instance id) and what it is ("granny", "grandpa"). */
	int hostId;
	String kind = "";
	/** Its own tick count at the last position from Granny: gone when she stops sending it (knocked down, plugin gone). */
	int seen;
	private EntityDimensions size;

	public StandIn(final EntityType<? extends StandIn> type, final Level level) {
		super(type, level);
		this.setNoAi(true);
		this.setNoGravity(true);
		this.setSilent(true);
		this.setPersistenceRequired();
	}

	static AttributeSupplier.Builder attributes() {
		return Mob.createMobAttributes().add(Attributes.MAX_HEALTH, 1024.0);
	}

	/** Width and height in blocks (Granny's body measured on her side). */
	void resize(final float width, final float height) {
		if (this.size == null || Math.abs(this.size.width() - width) > 0.05F || Math.abs(this.size.height() - height) > 0.05F) {
			this.size = EntityDimensions.scalable(width, height);
			this.refreshDimensions();
		}
	}

	@Override
	protected EntityDimensions getDefaultDimensions(final Pose pose) {
		return this.size != null ? this.size : super.getDefaultDimensions(pose);
	}

	@Override
	public boolean hurtServer(final ServerLevel level, final DamageSource source, final float amount) {
		// only mobs' hits: explosions are already reported to Granny (ServerExplosionMixin), the player's hits too
		if (!(source.getEntity() instanceof Mob mob) || mob instanceof StandIn || source.is(DamageTypeTags.IS_EXPLOSION) || amount <= 0.0F) {
			return false;
		}

		WorldBridge.standInHurt(this, mob, amount);
		return true;
	}

	@Override
	public void tick() {
		super.tick();
		if (!this.level().isClientSide() && this.tickCount - this.seen > 20) {
			this.discard(); // Granny stopped sending her: she is down, gone, or the plugin went away
		}
	}

	@Override
	public void travel(final Vec3 input) {
		// stays exactly where Granny's side puts it
	}

	@Override
	public void knockback(final double strength, final double x, final double z, final DamageSource source, final float amount, final boolean blocked) {
	}

	@Override
	public void knockback(final double strength, final double x, final double z, final DamageSource source, final float amount) {
	}

	@Override
	public boolean isPickable() {
		return false; // Steve's crosshair goes through: his hits on Granny are worked out on Granny's side
	}

	@Override
	public boolean canBeHitByProjectile() {
		return this.isAlive(); // mobs' arrows and fireballs hit her (the player's are filtered: ProjectileMixin)
	}

	@Override
	public boolean isPushable() {
		return false;
	}

	@Override
	protected void pushEntities() {
	}

	@Override
	protected void doPush(final Entity entity) {
	}

	@Override
	public boolean removeWhenFarAway(final double distance) {
		return false;
	}

	@Override
	public void checkDespawn() {
	}

	@Override
	public boolean canUsePortal(final boolean force) {
		return false;
	}

	@Override
	public boolean isCurrentlyGlowing() {
		return false;
	}

	@Override
	protected SoundEvent getAmbientSound() {
		return null;
	}

	@Override
	protected SoundEvent getHurtSound(final DamageSource source) {
		return null;
	}

	@Override
	protected SoundEvent getDeathSound() {
		return null;
	}
}
