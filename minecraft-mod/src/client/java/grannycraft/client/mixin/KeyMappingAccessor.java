package grannycraft.client.mixin;

import net.minecraft.client.KeyMapping;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

@Mixin(KeyMapping.class)
public interface KeyMappingAccessor {
	@Accessor("clickCount")
	int grannycraft$getClickCount();

	@Accessor("clickCount")
	void grannycraft$setClickCount(int clickCount);
}
