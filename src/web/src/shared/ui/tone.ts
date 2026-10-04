/** Tone triples from ui-design.md §3.1 (`--tone-<name>-bg/-ink/-border`). */
export type Tone = 'ok' | 'warn' | 'err' | 'ai' | 'human' | 'system' | 'pending'

/** The three actor inks: violet AI recommends, blue a person decides, grey the system executes. */
export type Actor = 'ai' | 'human' | 'system'

export interface Labelled {
  label: string
  tone: Tone
}
