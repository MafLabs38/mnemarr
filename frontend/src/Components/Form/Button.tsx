import type { ButtonHTMLAttributes } from 'react';

import styles from './Button.module.css';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** `danger` for what cannot be undone. */
  variant?: 'primary' | 'quiet' | 'danger';
}

export default function Button({ variant = 'primary', className, type = 'button', ...rest }: Readonly<ButtonProps>) {
  return (
    <button className={[styles.button, styles[variant], className].filter(Boolean).join(' ')} type={type} {...rest} />
  );
}
