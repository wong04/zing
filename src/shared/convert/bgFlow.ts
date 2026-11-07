/** Tells a run that started earlier that it is no longer the one wanted (Cancel, or another picture). */
export class RunGate {
	private current = 0

	begin(): number {
		return ++this.current
	}

	isCurrent(id: number): boolean {
		return id === this.current
	}

	cancel(): void {
		this.current++
	}
}

/**
 * What happens once the picture has been checked: ask the host whether the model is there, then offer it or run. The answer takes
 * time, and Cancel or another picture may come meanwhile; then nothing is offered or started.
 */
export async function proceedAfterCheck(
	gate: RunGate,
	id: number,
	model: () => Promise<{ readonly installed: boolean }>,
	offer: () => void,
	run: () => void,
): Promise<void> {
	const { installed } = await model()
	if (!gate.isCurrent(id)) return
	if (installed) run()
	else offer()
}
