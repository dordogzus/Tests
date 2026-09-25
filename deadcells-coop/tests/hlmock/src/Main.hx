// Minimal stand-in for a HashLink game. Class and method names follow the
// shape the Dead Cells hook table targets, so patching can be tested end to end.
// MOCK_FRAMES / MOCK_DX / MOCK_HIT_FRAME drive a real-time session for the
// two-process test; without them it runs 3 frames like the vanilla check.
class Main {
	static function main() {
		var frames = Std.parseInt(env("MOCK_FRAMES", "3"));
		var hitFrame = Std.parseInt(env("MOCK_HIT_FRAME", "-1"));
		Game.scripted = frames <= 3;
		var g = new Game();
		g.hero.dx = Std.parseFloat(env("MOCK_DX", "0.1"));
		for (f in 0...frames) {
			g.update();
			if (f == hitFrame) g.mob.hit(g.hero, 7);
			if (frames > 3) Sys.sleep(1 / 60);
		}
		var heroes = 0;
		for (e in Game.ALL) {
			var h = Std.downcast(e, en.Hero);
			if (h == null) continue;
			heroes++;
			Sys.println("hero " + (h == g.hero ? "local" : "ghost") + " x=" + Math.round((h.cx + h.xr) * 100) / 100);
		}
		Sys.println("heroes=" + heroes + " hero life=" + g.hero.life + " mob life=" + g.mob.life + " mob maxLife=" + g.mob.maxLife);
	}

	static function env(k:String, d:String) {
		var v = Sys.getEnv(k);
		return v == null ? d : v;
	}
}
