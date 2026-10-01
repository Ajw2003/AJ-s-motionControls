/**
 * Orders a scores dict for the phone scoreboard (#46): highest first, with standard
 * competition ranking (1, 2, 2, 4) so equal scores share a rank. Array.prototype.sort is
 * stable, so ties keep dict (join) order and rows don't jump between broadcasts.
 * @param {Record<string, number>} scores id -> score
 * @returns {{player: string, score: number, rank: number}[]}
 */
export function rankScores(scores) {
  const sorted = Object.entries(scores ?? {})
    .map(([player, score]) => ({ player, score }))
    .sort((a, b) => b.score - a.score);
  const ranked = [];
  sorted.forEach((entry, i) => {
    const tied = i > 0 && ranked[i - 1].score === entry.score;
    ranked.push({ ...entry, rank: tied ? ranked[i - 1].rank : i + 1 });
  });
  return ranked;
}
