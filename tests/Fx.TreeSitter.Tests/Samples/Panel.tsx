import { useState } from "react";

export interface PanelProps {
  title: string;
}

export function Panel({ title }: PanelProps) {
  const [open, setOpen] = useState(false);
  return (
    <section>
      <h2 onClick={() => setOpen(!open)}>{title}</h2>
      {open && <p>Ünïcödé body ✓</p>}
    </section>
  );
}

export const Footer = () => <footer>done</footer>;
