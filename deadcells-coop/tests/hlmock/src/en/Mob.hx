package en;

class Mob extends Entity {
	public function new(x, y, hp:Int) {
		super(x, y);
		life = maxLife = hp;
	}

	public function init() {}

	override function update() {
		super.update();
	}
}
