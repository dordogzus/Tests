package en;

class Entity {
	public var cx:Int;
	public var cy:Int;
	public var xr = 0.5;
	public var yr = 1.0;
	public var dx = 0.;
	public var dy = 0.;
	public var life:Int;
	public var maxLife:Int;

	public function new(x:Int, y:Int) {
		cx = x;
		cy = y;
		life = maxLife = 100;
		Game.ALL.push(this);
	}

	public function update() {
		xr += dx;
		while (xr >= 1) { xr -= 1; cx++; }
		while (xr < 0) { xr += 1; cx--; }
	}

	public function hit(from:Entity, dmg:Int) {
		life -= dmg;
	}

	public function destroy() {
		dispose();
	}

	public function dispose() {
		Game.ALL.remove(this);
	}
}
