interface BrandLogoProps {
  /** Height in pixels; the width follows the drawing's own proportions. */
  size: number;
}

/** The Mnemarr brain. Decorative: the product name is always next to it, so it has no text of its own. */
export default function BrandLogo({ size }: Readonly<BrandLogoProps>) {
  return <img alt="" height={size} src="/logo.svg" />;
}
