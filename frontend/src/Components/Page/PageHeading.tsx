import styles from './PageHeading.module.css';

interface PageHeadingProps {
  /** The section the page belongs to ("System"), shown small above the title. */
  scope: string;
  title: string;
}

export default function PageHeading({ scope, title }: Readonly<PageHeadingProps>) {
  return (
    <div className={styles.heading}>
      <span className={styles.scope}>{scope}</span>
      <h1 className={styles.title}>{title}</h1>
    </div>
  );
}
