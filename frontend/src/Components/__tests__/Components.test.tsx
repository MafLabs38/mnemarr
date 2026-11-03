import { render, screen } from '@testing-library/react';

import Alert from 'Components/Alert/Alert';
import Card from 'Components/Card';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import LoadingIndicator from 'Components/LoadingIndicator';
import StatusLabel from 'Components/Status/StatusLabel';
import { kinds } from 'Helpers/Props/kinds';

describe('Card', () => {
  it('names its region after its title', () => {
    render(<Card title="About">content</Card>);

    expect(screen.getByRole('region', { name: 'About' })).toHaveTextContent('content');
  });

  it('is not a named region without a title', () => {
    render(<Card>content</Card>);

    expect(screen.queryByRole('region')).not.toBeInTheDocument();
  });

  it('keeps two cards with the same title apart', () => {
    render(
      <>
        <Card title="Same">first</Card>
        <Card title="Same">second</Card>
      </>,
    );

    const [first, second] = screen.getAllByRole('region', { name: 'Same' });
    expect(first).toHaveTextContent('first');
    expect(second).toHaveTextContent('second');
  });

  it('names its region even when the title has no ASCII letters', () => {
    render(<Card title="日本語">content</Card>);

    expect(screen.getByRole('region', { name: '日本語' })).toBeInTheDocument();
  });
});

describe('DescriptionList', () => {
  it('pairs each title with its data', () => {
    render(
      <DescriptionList
        items={[
          { title: 'Version', data: '1.0' },
          { title: 'Runtime', data: '.NET', mono: true },
        ]}
      />,
    );

    expect(screen.getByText('Version').tagName).toBe('DT');
    expect(screen.getByText('1.0').tagName).toBe('DD');
    expect(screen.getByText('.NET').tagName).toBe('DD');
  });
});

describe('StatusLabel', () => {
  it('shows each kind with an icon that is not read, and the text', () => {
    const { container } = render(
      <>
        <StatusLabel kind={kinds.SUCCESS}>Fine</StatusLabel>
        <StatusLabel kind={kinds.DANGER}>Broken</StatusLabel>
      </>,
    );

    expect(screen.getByText('Fine')).toBeInTheDocument();
    expect(screen.getByText('Broken')).toBeInTheDocument();
    expect(container.querySelectorAll('svg[aria-hidden="true"]')).toHaveLength(2);
  });
});

describe('Alert and LoadingIndicator', () => {
  it('announces an error', () => {
    render(<Alert>It broke</Alert>);

    expect(screen.getByRole('alert')).toHaveTextContent('It broke');
  });

  it('announces that it is loading', () => {
    render(<LoadingIndicator />);

    expect(screen.getByRole('status')).toHaveTextContent('Loading…');
  });
});
