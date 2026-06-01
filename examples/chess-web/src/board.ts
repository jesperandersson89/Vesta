/** Chess board renderer with click-to-move handling. Uses chess.js for rules. */

import { Chess, type Square } from "chess.js";

const PIECE_GLYPHS: Record<string, string> = {
  P: "♙", N: "♘", B: "♗", R: "♖", Q: "♕", K: "♔",
  p: "♟", n: "♞", b: "♝", r: "♜", q: "♛", k: "♚",
};

const FILES = ["a", "b", "c", "d", "e", "f", "g", "h"] as const;

export interface BoardOptions {
  /** Which color this player controls (board rendered from that perspective). */
  orientation: "w" | "b";
  /** Called when the user attempts a move. Return false to reject visually. */
  onMove(from: Square, to: Square, promotion?: "q" | "r" | "b" | "n"): boolean;
  /** Whether this player may currently move (their turn + game not over). */
  isMyTurn(): boolean;
}

export class BoardView {
  private readonly root: HTMLElement;
  private readonly opts: BoardOptions;
  private chess = new Chess();
  private selected: Square | null = null;
  private lastMove: { from: Square; to: Square } | null = null;

  constructor(root: HTMLElement, opts: BoardOptions) {
    this.root = root;
    this.opts = opts;
    this.render();
  }

  /** Replace the board state from a FEN. */
  setFen(fen: string): void {
    this.chess.load(fen);
    this.selected = null;
    this.render();
  }

  /** Apply a SAN-style move to the local board (used to mirror remote moves). */
  applyMove(from: Square, to: Square, promotion?: "q" | "r" | "b" | "n"): boolean {
    const result = this.chess.move({ from, to, promotion: promotion ?? "q" });
    if (!result) return false;
    this.lastMove = { from, to };
    this.selected = null;
    this.render();
    return true;
  }

  get fen(): string {
    return this.chess.fen();
  }

  get turn(): "w" | "b" {
    return this.chess.turn();
  }

  get isGameOver(): boolean {
    return this.chess.isGameOver();
  }

  get statusText(): string {
    if (this.chess.isCheckmate()) return `Checkmate — ${this.turn === "w" ? "Black" : "White"} wins`;
    if (this.chess.isStalemate()) return "Stalemate";
    if (this.chess.isDraw()) return "Draw";
    if (this.chess.isCheck()) return `Check (${this.turn === "w" ? "White" : "Black"} to move)`;
    return `${this.turn === "w" ? "White" : "Black"} to move`;
  }

  private render(): void {
    this.root.innerHTML = "";

    const ranks = this.opts.orientation === "w" ? [8, 7, 6, 5, 4, 3, 2, 1] : [1, 2, 3, 4, 5, 6, 7, 8];
    const files = this.opts.orientation === "w" ? FILES : [...FILES].reverse();

    const targets = new Set<Square>();
    if (this.selected) {
      for (const m of this.chess.moves({ square: this.selected, verbose: true })) {
        targets.add(m.to as Square);
      }
    }

    for (const rank of ranks) {
      for (const file of files) {
        const square = (file + rank) as Square;
        const isDark = (FILES.indexOf(file) + rank) % 2 === 0;
        const cell = document.createElement("div");
        cell.className = `square ${isDark ? "dark" : "light"}`;
        cell.dataset.square = square;

        if (this.selected === square) cell.classList.add("selected");
        if (targets.has(square)) cell.classList.add("move-target", isDark ? "dark" : "light");
        if (this.lastMove && (this.lastMove.from === square || this.lastMove.to === square)) {
          cell.classList.add("last-move");
        }

        const piece = this.chess.get(square);
        if (piece) {
          const glyph = PIECE_GLYPHS[piece.color === "w" ? piece.type.toUpperCase() : piece.type];
          cell.textContent = glyph ?? "";
          cell.classList.add(piece.color === "w" ? "piece-white" : "piece-black");
        }

        cell.addEventListener("click", () => this.handleClick(square));
        this.root.appendChild(cell);
      }
    }
  }

  private handleClick(square: Square): void {
    if (!this.opts.isMyTurn()) return;

    const piece = this.chess.get(square);

    // Selecting one of my pieces
    if (piece && piece.color === this.opts.orientation) {
      this.selected = square;
      this.render();
      return;
    }

    if (!this.selected) return;

    // Attempt move
    const legal = this.chess.moves({ square: this.selected, verbose: true }).some((m) => m.to === square);
    if (!legal) {
      this.selected = null;
      this.render();
      return;
    }

    const from = this.selected;
    // Auto-promote to queen for simplicity.
    const accepted = this.opts.onMove(from, square, "q");
    if (accepted) {
      this.applyMove(from, square, "q");
    }
  }
}
