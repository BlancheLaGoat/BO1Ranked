// ranked_rng.gsc
// Aleatoire deterministe pour le mod ranked (BO1 Zombies / Plutonium).
// Meme seed + meme numero d'evenement = meme resultat chez les deux joueurs.
//
// Seed : fichier ranked/match.txt ecrit par l'app compagnon (necessite le plugin t5-gsc-utils),
// sinon dvar "ranked_seed" (1 a 999999), sinon une seed tiree au hasard. Elle est affichee en debut de partie.
//
// Generateur : L'Ecuyer combine 16 bits. Uniquement des multiplications et des modulos
// sur de petits entiers, donc aucun depassement et aucune division flottante en GSC.

#include maps\_utility;
#include common_scripts\utility;

rr_init()
{
	if ( isDefined( level.rr_seed ) )
	{
		return;
	}

	// Priorite au match en cours : l'app compagnon ecrit "seed;objectif" dans ranked/match.txt.
	seed = 0;
	if ( fileExists( "ranked/match.txt" ) )
	{
		tokens = strTok( readFile( "ranked/match.txt" ), ";" );
		if ( tokens.size >= 1 )
		{
			seed = int( tokens[0] );
		}
	}

	if ( seed <= 0 )
	{
		seed = getDvarInt( "ranked_seed" );
	}
	if ( seed <= 0 )
	{
		seed = randomInt( 999999 ) + 1;
	}

	level.rr_seed = seed % 1000000;
	level.rr_streams = [];

	level thread rr_show_seed();
}

rr_show_seed()
{
	wait 12;
	iPrintLn( "Ranked seed: " + level.rr_seed );
}

// Un identifiant fixe par systeme. Ne jamais changer un numero existant,
// sinon les seeds ne donnent plus les memes parties.
rr_stream_id( name )
{
	switch ( name )
	{
		case "box":			return 1;	// arme tiree
		case "box_move":	return 2;	// ours en peluche
		case "box_loc":		return 3;	// emplacements de la box
		case "drops":		return 4;	// power-ups
		case "special":		return 5;	// rounds speciaux
		case "drop_roll":	return 6;	// chance de drop a chaque kill
	}
	return 99;
}

rr_stream( name )
{
	rr_init();

	if ( !isDefined( level.rr_streams[name] ) )
	{
		s = spawnStruct();
		s.id = rr_stream_id( name );
		s.event = 0;
		level.rr_streams[name] = s;
		rr_reseed( s );
	}
	return level.rr_streams[name];
}

// Recalcule l'etat du flux a partir de (seed, systeme, numero d'evenement).
rr_reseed( s )
{
	seed = level.rr_seed;
	n = s.event % 20000;

	s.s1 = 1 + ( seed + s.id * 7919 + n * 9973 ) % 32362;
	s.s2 = 1 + ( seed * 7 + s.id * 6151 + n * 8191 + 12345 ) % 31726;
	s.s3 = 1 + ( seed * 13 + s.id * 3571 + n * 7307 + 23456 ) % 31656;

	for ( i = 0; i < 10; i++ )
	{
		rr_step( s );
	}
}

rr_step( s )
{
	s.s1 = ( 157 * s.s1 ) % 32363;
	s.s2 = ( 146 * s.s2 ) % 31727;
	s.s3 = ( 142 * s.s3 ) % 31657;

	z = ( s.s1 - s.s2 + s.s3 ) % 32362;
	if ( z < 0 )
	{
		z += 32362;
	}
	return z;
}

// Demarre l'evenement suivant d'un systeme (ex : le tirage de box numero N).
// Tout ce qui est tire ensuite ne depend que de la seed et de N,
// pas de ce qui s'est passe avant dans la partie.
rr_begin_event( name )
{
	s = rr_stream( name );
	s.event++;
	rr_reseed( s );
	return s.event;
}

// Entier dans [0, 32361]
rr_next( name )
{
	return rr_step( rr_stream( name ) );
}

// Equivalent de RandomInt( max ), max <= 32362
rr_randomint( name, max )
{
	if ( max <= 1 )
	{
		return 0;
	}
	return rr_next( name ) % max;
}

// Equivalent de RandomIntRange( min, max )
rr_randomintrange( name, min, max )
{
	return min + rr_randomint( name, max - min );
}

// Nouvel evenement + un seul tirage (ex : l'ours, un drop)
rr_event_randomint( name, max )
{
	rr_begin_event( name );
	return rr_randomint( name, max );
}

// Nouvel evenement + un seul tirage dans [min, max[ (ex : prochain round de chiens)
rr_event_randomintrange( name, min, max )
{
	rr_begin_event( name );
	return rr_randomintrange( name, min, max );
}

// Equivalent de array_randomize( array )
rr_array_randomize( name, array )
{
	for ( i = array.size - 1; i > 0; i-- )
	{
		j = rr_randomint( name, i + 1 );
		temp = array[i];
		array[i] = array[j];
		array[j] = temp;
	}
	return array;
}

// ---------------------------------------------------------------------------
// BOX
// ---------------------------------------------------------------------------
// "filtered" = la liste ponderee construite par le jeu dans
// treasure_chest_ChooseWeightedRandomWeapon (armes valides pour ce joueur,
// chaque arme repetee selon son poids).
//
// Pour le tirage numero N, chaque arme du jeu recoit un score qui ne depend que
// de la seed et de N. Le joueur recoit l'arme valide qui a le meilleur score.
// Deux joueurs avec le meme inventaire ont donc la meme arme, et s'ils different,
// seul le joueur qui possede deja l'arme gagnante passe a la suivante.
// Les poids du jeu (ray gun, singes) sont respectes jusqu'a max_weight.
rr_pick_box_weapon( filtered )
{
	max_weight = 6;

	rr_begin_event( "box" );

	keys = getArrayKeys( level.zombie_weapons );
	best = undefined;
	best_score = -1;

	for ( i = 0; i < keys.size; i++ )
	{
		weight = 0;
		for ( j = 0; j < filtered.size; j++ )
		{
			if ( filtered[j] == keys[i] )
			{
				weight++;
			}
		}
		if ( weight > max_weight )
		{
			weight = max_weight;
		}

		// Toujours le meme nombre de tirages par arme, valide ou non,
		// pour que les scores des armes suivantes ne se decalent pas.
		score = -1;
		for ( d = 0; d < max_weight; d++ )
		{
			v = rr_next( "box" );
			if ( d < weight && v > score )
			{
				score = v;
			}
		}

		if ( score > best_score )
		{
			best_score = score;
			best = keys[i];
		}
	}

	return best;
}
