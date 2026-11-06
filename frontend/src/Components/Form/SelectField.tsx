import type { SelectHTMLAttributes } from 'react';

import styles from './Field.module.css';

interface SelectFieldProps extends SelectHTMLAttributes<HTMLSelectElement> {
  label: string;
  options: ReadonlyArray<{ value: string; label: string }>;
}

export default function SelectField({ label, options, ...select }: Readonly<SelectFieldProps>) {
  return (
    <label className={styles.field}>
      <span className={styles.label}>{label}</span>
      <select className={styles.control} {...select}>
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </label>
  );
}
