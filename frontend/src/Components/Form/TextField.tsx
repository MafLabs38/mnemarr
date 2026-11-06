import type { InputHTMLAttributes } from 'react';

import styles from './Field.module.css';

interface TextFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
}

/** A labelled input: the label wraps the control, so it is always the accessible name of the field. */
export default function TextField({ label, ...input }: Readonly<TextFieldProps>) {
  return (
    <label className={styles.field}>
      <span className={styles.label}>{label}</span>
      <input className={styles.control} {...input} />
    </label>
  );
}
