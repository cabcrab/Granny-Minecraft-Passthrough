package grannycraft.mixin;

import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;
import org.spongepowered.asm.mixin.gen.Invoker;

@Mixin(AbstractArrow.class)
public interface ArrowAccessor {
	@Accessor("baseDamage")
	double grannycraft$baseDamage();

	@Invoker("isInGround")
	boolean grannycraft$inGround();
}
