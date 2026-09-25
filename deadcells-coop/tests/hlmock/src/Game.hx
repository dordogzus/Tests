class Game {
	public static var ALL:Array<en.Entity> = [];
	public static var scripted = true; // early scripted hits (vanilla 3-frame check only)
	public var hero:en.Hero;
	public var mob:en.Mob;
	public var frame = 0;

	public function new() {
		hero = new en.Hero(10, 5);
		mob = new en.Mob(14, 5, 100);
		onLevelStart();
	}

	function onLevelStart() {
		mob.init();
	}

	public function update() {
		frame++;
		for (e in ALL.copy()) e.update();
		if (scripted && frame <= 3) {
			mob.hit(hero, 10);
			hero.hit(mob, 7);
		}
	}
}
