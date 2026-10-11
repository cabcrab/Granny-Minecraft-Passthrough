package grannycraft.client;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import grannycraft.GrannyCraft;
import grannycraft.WorldBridge;
import java.net.InetSocketAddress;
import java.util.Locale;
import net.minecraft.client.Minecraft;
import org.java_websocket.WebSocket;
import org.java_websocket.handshake.ClientHandshake;
import org.java_websocket.server.WebSocketServer;

/**
 * The host's connection: a WebSocket server on 127.0.0.1 (port 25600, or -Dgrannycraft.port).
 *
 * <p>Host to Minecraft (JSON, Minecraft coordinates):
 * <ul>
 * <li>{"t":"cam","f":frame,"p":[x,y,z],"r":[yaw,pitch,roll],"fov":vertical degrees,"fp":first person,"pl":[feet x,y,z],"h":body yaw}</li>
 * <li>{"t":"ground","c":[x,z,yBottom,yTop, ...]}: solid columns (barriers)</li>
 * <li>{"t":"clear"}: remove the barriers placed so far</li>
 * <li>{"t":"cmd","c":"time set noon"}: a server command</li>
 * <li>{"t":"key","k":"use|attack|pick|inventory|drop|swap|escape","down":bool}</li>
 * <li>{"t":"slot","n":0-8}, {"t":"scroll","d":+-1}, {"t":"hud","hidden":bool}, {"t":"view","w":px,"h":px}</li>
 * <li>{"t":"menu","on":bool} and the pointer while it is open: see {@link CreativeMenu}</li>
 * <li>{"t":"nethercfg","secs":seconds,"r":blocks}: how long and how far the Nether comes (NetherZone)</li>
 * <li>{"t":"standins","s":[[id,kind,x,y,z,w,h,yaw],...]}: Granny and Grandpa for hostile mobs to fight (StandIn)</li>
 * <li>{"t":"mobhit","id":mob,"d":damage,"by":id}: Granny or Grandpa hit a mob</li>
 * </ul>
 * Minecraft to host: {"t":"hello",...} on connect, {"t":"explosion","pos":[x,y,z],"r":radius},
 * {"t":"menu","on":bool}, {"t":"nether","on":bool,"secs":seconds}, {"t":"mobs","m":[...]} (hostile mobs around),
 * {"t":"hurt","id":id,"d":damage,"by":mob,"from":[x,y,z]} (a mob hit Granny or Grandpa).
 */
public final class HostLink extends WebSocketServer {
	private static HostLink instance;

	private HostLink(final int port) {
		super(new InetSocketAddress("127.0.0.1", port));
		this.setReuseAddr(true);
		this.setDaemon(true);
	}

	static void launch() {
		int port = Integer.getInteger("grannycraft.port", 25600);
		instance = new HostLink(port);
		instance.start();
		GrannyCraft.events = message -> instance.broadcast(message);
	}

	@Override
	public void onStart() {
		GrannyCraft.LOG.info("host link listening on 127.0.0.1:{}", this.getPort());
	}

	@Override
	public void onOpen(final WebSocket conn, final ClientHandshake handshake) {
		GrannyCraftClient.hotbarChanged();
		GrannyCraft.LOG.info("host connected from {}", conn.getRemoteSocketAddress());
		conn.send(String.format(Locale.ROOT, "{\"t\":\"hello\",\"v\":1,\"shm\":\"%s\",\"pid\":%d}", FrameExporter.NAME.replace("\\", "\\\\"), ProcessHandle.current().pid()));
	}

	@Override
	public void onClose(final WebSocket conn, final int code, final String reason, final boolean remote) {
		GrannyCraft.LOG.info("host disconnected ({} {})", code, reason);
	}

	@Override
	public void onMessage(final WebSocket conn, final String message) {
		try {
			JsonObject m = JsonParser.parseString(message).getAsJsonObject();
			switch (m.get("t").getAsString()) {
				case "cam" -> HostState.update(m);
				case "ghost" -> WorldBridge.ghosts(longs(m.getAsJsonArray("c")));
				case "projhit" -> {
					JsonArray at = m.has("pos") ? m.getAsJsonArray("pos") : null;
					WorldBridge.arrowHit(m.get("id").getAsInt(), at == null ? null : new double[] {at.get(0).getAsDouble(), at.get(1).getAsDouble(), at.get(2).getAsDouble()});
				}
				case "gitem" -> grannycraft.GrannyItems.give(m.get("name").getAsString());
				case "gitem_clear" -> grannycraft.GrannyItems.clear();
				case "uncarve" -> {
					// pieces of house grown back: they become house again (Granny re-sends their shape right after)
					JsonArray c = m.getAsJsonArray("c");
					for (int i = 0; i + 2 < c.size(); i += 3) {
						grannycraft.GhostBlock.BROKEN.remove(net.minecraft.core.BlockPos.asLong(c.get(i).getAsInt(), c.get(i + 1).getAsInt(), c.get(i + 2).getAsInt()));
					}
				}
				case "breakable" -> {
					grannycraft.GhostBlock.BREAKABLE = m.get("on").getAsBoolean();
					GrannyCraft.LOG.info("breakable house: {}", grannycraft.GhostBlock.BREAKABLE);
				}
				case "cmd" -> WorldBridge.command(m.get("c").getAsString());
				case "standins" -> WorldBridge.standIns(m.getAsJsonArray("s"));
				case "mobhit" -> WorldBridge.mobHit(m.get("id").getAsInt(), m.get("d").getAsFloat(), m.get("by").getAsInt());
				case "nethercfg" -> {
					grannycraft.NetherZone.seconds = Math.clamp(m.get("secs").getAsInt(), 1, 3600);
					grannycraft.NetherZone.radius = Math.clamp(m.get("r").getAsInt(), 4, 32);
				}
				case "newgame" -> {
					WorldBridge.newGame();
					GrannyCraftClient.resetKit();
				}
				case "blocksync" -> WorldBridge.sync(m.has("r") ? m.get("r").getAsInt() : 32);
				case "passthrough" -> GrannyCraft.LOG.info("passthrough {}", m.get("on").getAsBoolean() ? "on" : "off");
				default -> {
					Minecraft minecraft = Minecraft.getInstance();
					minecraft.execute(() -> ClientInput.handle(minecraft, m));
				}
			}
		} catch (RuntimeException e) {
			GrannyCraft.LOG.warn("bad host message {}: {}", message.length() > 200 ? message.substring(0, 200) : message, e.toString());
		}
	}

	private static long[] longs(final JsonArray a) {
		long[] out = new long[a.size()];
		for (int i = 0; i < out.length; i++) {
			out[i] = a.get(i).getAsLong();
		}

		return out;
	}

	/** Messages between the host plugin and a director script pass through Minecraft's link to every other client. */
	private void relay(final WebSocket from, final String message) {
		for (WebSocket c : this.getConnections()) {
			if (c != from && c.isOpen()) {
				c.send(message);
			}
		}
	}

	@Override
	public void onError(final WebSocket conn, final Exception e) {
		GrannyCraft.LOG.warn("host link error", e);
	}

	private static int[] ints(final JsonArray a) {
		int[] out = new int[a.size()];
		for (int i = 0; i < out.length; i++) {
			out[i] = a.get(i).getAsInt();
		}

		return out;
	}
}
